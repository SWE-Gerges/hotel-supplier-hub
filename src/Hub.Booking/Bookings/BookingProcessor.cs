using System.Security.Cryptography;
using System.Text;
using Hub.Abstractions;
using Hub.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hub.Booking;

/// <summary>
/// Turns a quote into a booking exactly once. The (tenant, idempotency key) pair is claimed in the database together with
/// the quote, the supplier is asked at most once per claim, and a booking whose outcome is not known is settled by
/// asking the supplier what it has, never by booking again.
/// </summary>
public sealed class BookingProcessor(
    IBookingStore bookings,
    IQuoteStore quotes,
    QuoteService quoteService,
    ISupplierRegistry registry,
    ISupplierGateway gateway,
    IOptions<BookingOptions> options,
    TimeProvider time,
    ILogger<BookingProcessor> logger)
{
    private readonly StripedLock _locks = new(64);

    public static string ReferenceFor(string tenant, string idempotencyKey) =>
        "hub-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}:{idempotencyKey}")))[..24];

    public async Task<BookingCreateOutcome> CreateAsync(CreateBookingCommand command, CancellationToken cancellationToken)
    {
        var hash = PayloadHash(command);
        if (await bookings.GetByKeyAsync(command.Tenant, command.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } earlier)
        {
            return AlreadyClaimed(earlier, hash);
        }

        if (await quotes.GetAsync(command.Tenant, command.QuoteId, cancellationToken).ConfigureAwait(false) is not { } quote)
        {
            return new BookingCreateOutcome(BookingCreateStatus.QuoteNotFound, null, null, "No quote with that id exists for this tenant.");
        }

        var now = time.GetUtcNow();
        if (quote.Status != QuoteStatus.Active || quote.ExpiresAt <= now)
        {
            // A request with the same key may have used the quote in between; its booking and the quote change commit together,
            // so if the quote is used now the booking is visible now.
            if (await bookings.GetByKeyAsync(command.Tenant, command.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } raced)
            {
                return AlreadyClaimed(raced, hash);
            }

            return await StaleQuoteAsync(quote, now, cancellationToken).ConfigureAwait(false);
        }

        if (command.Currency != quote.SellingCurrency || command.Amount != quote.SellingAmount)
        {
            return new BookingCreateOutcome(
                BookingCreateStatus.PriceChanged, null, QuoteService.ToView(quote, null),
                $"The quote is for {quote.Selling}, not {command.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} {command.Currency}.");
        }

        var settings = options.Value;
        DateTimeOffset? payBy = null;
        if (command.PaymentMode == PaymentMode.PayLater)
        {
            if (!quote.PolicyKnown || !quote.Refundable || quote.FreeCancellationUntil is not { } until || until - now < settings.PayLaterMinimumLead)
            {
                return new BookingCreateOutcome(
                    BookingCreateStatus.PayLaterNotAllowed, null, null,
                    $"Pay later needs a known free-cancellation deadline at least {settings.PayLaterMinimumLead.TotalHours:0.#} hours away.");
            }

            payBy = until - settings.PayLaterCancelBuffer;
        }

        if (!registry.TryGet(Supplier(quote), out var entry) || entry.Book is null)
        {
            return new BookingCreateOutcome(BookingCreateStatus.Unsupported, null, null, $"Supplier '{quote.Supplier}' cannot take bookings.");
        }

        var offer = Snapshot.Read<RoomOffer>(quote.OfferJson);
        var booking = new BookingRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Tenant = command.Tenant,
            IdempotencyKey = command.IdempotencyKey,
            PayloadHash = hash,
            QuoteId = quote.Id,
            OfferId = quote.OfferId,
            Supplier = quote.Supplier,
            SupplierHotelCode = offer.SupplierHotelCode,
            SupplierReference = ReferenceFor(command.Tenant, command.IdempotencyKey),
            Status = HubBookingStatus.Pending,
            SellingAmount = quote.SellingAmount,
            SellingCurrency = quote.SellingCurrency,
            SupplierAmount = quote.SupplierAmount,
            SupplierCurrency = quote.SupplierCurrency,
            LeadGuestName = command.LeadGuestName,
            PaymentMode = command.PaymentMode,
            FreeCancellationUntil = quote.FreeCancellationUntil,
            PayBy = payBy,
            CreatedAt = now,
            UpdatedAt = now,
            OfferJson = quote.OfferJson,
            SearchJson = quote.SearchJson,
        };

        switch (await bookings.ClaimAsync(booking, now, cancellationToken).ConfigureAwait(false))
        {
            case ClaimOutcome.DuplicateKey:
                var winner = await bookings.GetByKeyAsync(command.Tenant, command.IdempotencyKey, cancellationToken).ConfigureAwait(false)
                             ?? throw new InvalidOperationException("The idempotency key is claimed but its booking cannot be read.");
                return AlreadyClaimed(winner, hash);
            case ClaimOutcome.QuoteNotLive:
                return await bookings.GetByKeyAsync(command.Tenant, command.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } existingAfter
                    ? AlreadyClaimed(existingAfter, hash)
                    : await StaleQuoteAsync(quote, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        // The claim is committed, so the supplier call and the state it leads to are finished whatever the client does next.
        using (await _locks.AcquireAsync(booking.Id, CancellationToken.None).ConfigureAwait(false))
        {
            await AttemptBookAsync(booking, offer).ConfigureAwait(false);
        }

        return new BookingCreateOutcome(BookingCreateStatus.Created, booking, null, null);
    }

    public Task<BookingRecord?> GetAsync(string tenant, string id, CancellationToken cancellationToken) =>
        bookings.GetAsync(tenant, id, cancellationToken);

    /// <summary>
    /// Brings a pending or unknown booking up to date with what the supplier holds. An unknown booking is looked up by the
    /// reference the hub sent; only if the supplier proves it has no such booking (and can de-duplicate) is it booked again.
    /// </summary>
    public async Task<BookingRecord?> SyncAsync(string tenant, string id, CancellationToken cancellationToken)
    {
        if (await bookings.GetAsync(tenant, id, cancellationToken).ConfigureAwait(false) is not { } booking)
        {
            return null;
        }

        return await SyncAsync(booking, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<BookingRecord> SyncAsync(BookingRecord seen, CancellationToken cancellationToken)
    {
        using var _ = await _locks.AcquireAsync(seen.Id, cancellationToken).ConfigureAwait(false);
        var booking = await bookings.GetAsync(seen.Tenant, seen.Id, cancellationToken).ConfigureAwait(false) ?? seen;
        if (booking.Status is not (HubBookingStatus.Pending or HubBookingStatus.Unknown))
        {
            return booking;
        }

        if (!registry.TryGet(Supplier(booking), out var entry))
        {
            return booking;
        }

        if (booking.Status == HubBookingStatus.Pending && booking.SupplierBookingRef is null)
        {
            // The request that claimed it never recorded an answer, for instance because the process stopped mid-call.
            if (time.GetUtcNow() - booking.UpdatedAt >= options.Value.InFlightTimeout)
            {
                await ChangeAsync(booking, b =>
                {
                    b.Status = HubBookingStatus.Unknown;
                    b.Note = "The booking request never recorded an answer; the supplier is asked what it has.";
                }).ConfigureAwait(false);
            }

            return booking;
        }

        if (entry.Retrieve is null)
        {
            await ChangeAsync(booking, b => b.Note = "The supplier cannot look bookings up, so this booking needs a manual check; it is not booked again.").ConfigureAwait(false);
            return booking;
        }

        var retrieved = await gateway.CallAsync(
            Supplier(booking),
            SupplierOperation.Retrieve,
            (e, ct) => e.Retrieve!.RetrieveAsync(new RetrieveRequest(booking.SupplierBookingRef, booking.SupplierReference), ct),
            cancellationToken).ConfigureAwait(false);

        if (retrieved.IsSuccess)
        {
            await ApplyConfirmationAsync(booking, retrieved.Value).ConfigureAwait(false);
        }
        else if (retrieved.Error.Kind == SupplierErrorKind.NotFound)
        {
            await SettleAbsentAsync(booking, entry).ConfigureAwait(false);
        }
        else
        {
            await ChangeAsync(booking, b => b.Note = $"Could not ask the supplier: {retrieved.Error.Message}").ConfigureAwait(false);
        }

        return booking;
    }

    public async Task<BookingCancelResult> CancelAsync(string tenant, string id, string? reason, CancellationToken cancellationToken)
    {
        if (await bookings.GetAsync(tenant, id, cancellationToken).ConfigureAwait(false) is not { } seen)
        {
            return new BookingCancelResult(null, null, BookingCancelRefusal.NotFound);
        }

        using var _ = await _locks.AcquireAsync(seen.Id, cancellationToken).ConfigureAwait(false);
        var booking = await bookings.GetAsync(tenant, id, cancellationToken).ConfigureAwait(false) ?? seen;
        if (booking.Status == HubBookingStatus.Cancelled)
        {
            return new BookingCancelResult(new BookingCancelOutcome(BookingCancelStatus.AlreadyCancelled, null, null, null), booking, null);
        }

        if (booking.Status is HubBookingStatus.Unknown or HubBookingStatus.Failed || booking.SupplierBookingRef is null)
        {
            return new BookingCancelResult(null, booking, BookingCancelRefusal.NotCancellable);
        }

        if (!registry.TryGet(Supplier(booking), out var entry) || entry.Cancel is null)
        {
            return new BookingCancelResult(new BookingCancelOutcome(BookingCancelStatus.Failed, null, null, "The supplier cannot cancel bookings."), booking, null);
        }

        var result = await gateway.CallAsync(
            Supplier(booking),
            SupplierOperation.Cancel,
            (e, ct) => e.Cancel!.CancelAsync(new CancelRequest(booking.SupplierBookingRef), ct),
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            // Nothing is assumed: the booking stays as it was and cancelling again is safe, because suppliers answer "already cancelled".
            return new BookingCancelResult(new BookingCancelOutcome(BookingCancelStatus.Failed, null, null, result.Error.Message), booking, null);
        }

        var supplierOutcome = result.Value;
        var penalty = supplierOutcome.Penalty is { } supplierPenalty ? ToSelling(booking, supplierPenalty) : (Money?)null;
        Money? refund = booking.PaidAt is not null
            ? new Money(Math.Max(0m, booking.SellingAmount - (penalty?.Amount ?? 0m)), booking.SellingCurrency)
            : null;
        var status = supplierOutcome.Status switch
        {
            SupplierCancelStatus.PenaltyApplied => BookingCancelStatus.PenaltyApplied,
            SupplierCancelStatus.AlreadyCancelled => BookingCancelStatus.AlreadyCancelled,
            _ => BookingCancelStatus.Cancelled,
        };
        await ChangeAsync(booking, b =>
        {
            b.Status = HubBookingStatus.Cancelled;
            b.CancelStatus = status.ToString();
            b.PenaltyAmount = penalty?.Amount;
            b.RefundAmount = refund?.Amount;
            b.CancelledAt = time.GetUtcNow();
            b.Note = reason ?? b.Note;
        }).ConfigureAwait(false);
        return new BookingCancelResult(new BookingCancelOutcome(status, penalty, refund, null), booking, null);
    }

    /// <summary>Records that the booking was paid. Returns null when the booking does not exist, false when it is cancelled or failed.</summary>
    public async Task<(BookingRecord? Booking, bool Accepted)> MarkPaidAsync(string tenant, string id, CancellationToken cancellationToken)
    {
        if (await bookings.GetAsync(tenant, id, cancellationToken).ConfigureAwait(false) is not { } seen)
        {
            return (null, false);
        }

        using var _ = await _locks.AcquireAsync(seen.Id, cancellationToken).ConfigureAwait(false);
        var booking = await bookings.GetAsync(tenant, id, cancellationToken).ConfigureAwait(false) ?? seen;
        if (booking.Status is HubBookingStatus.Cancelled or HubBookingStatus.Failed)
        {
            return (booking, false);
        }

        if (booking.PaidAt is null)
        {
            await ChangeAsync(booking, b => b.PaidAt = time.GetUtcNow()).ConfigureAwait(false);
        }

        return (booking, true);
    }

    internal async Task<IReadOnlyList<BookingRecord>> SweepAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var due = await bookings.ListByStatusAsync(
            [HubBookingStatus.Pending, HubBookingStatus.Unknown], time.GetUtcNow() - settings.SyncMinimumAge, settings.SweepBatchSize, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<BookingRecord>(due.Count);
        foreach (var booking in due)
        {
            try
            {
                results.Add(await SyncAsync(booking, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException)
            {
                logger.LogDebug(ex, "Booking {BookingId} changed while it was being synchronised; the next sweep looks at it again.", booking.Id);
            }
        }

        return results;
    }

    internal async Task<int> ExpireUnpaidAsync(CancellationToken cancellationToken)
    {
        var due = await bookings.ListPayLaterDueAsync(time.GetUtcNow(), options.Value.SweepBatchSize, cancellationToken).ConfigureAwait(false);
        var cancelled = 0;
        foreach (var booking in due)
        {
            var result = await CancelAsync(booking.Tenant, booking.Id, "Cancelled because payment was not received before the free-cancellation window closes.", cancellationToken).ConfigureAwait(false);
            if (result.Outcome?.Status is BookingCancelStatus.Cancelled or BookingCancelStatus.AlreadyCancelled or BookingCancelStatus.PenaltyApplied)
            {
                cancelled++;
                logger.LogInformation("Booking {BookingId} was cancelled unpaid ahead of its penalty window.", booking.Id);
            }
            else
            {
                logger.LogWarning("Booking {BookingId} is unpaid and due but could not be cancelled yet: {Detail}.", booking.Id, result.Outcome?.Detail ?? result.Refusal?.ToString());
            }
        }

        return cancelled;
    }

    private async Task AttemptBookAsync(BookingRecord booking, RoomOffer offer)
    {
        var search = Snapshot.Read<SupplierSearchQuery>(booking.SearchJson);
        booking.BookAttempts++;
        var result = await gateway.CallAsync(
            Supplier(booking),
            SupplierOperation.Book,
            (e, ct) => e.Book!.BookAsync(new BookRequest(new OfferRef(offer, search), booking.SupplierPrice, booking.SupplierReference, booking.LeadGuestName), ct),
            CancellationToken.None).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await ApplyConfirmationAsync(booking, result.Value).ConfigureAwait(false);
            return;
        }

        var error = result.Error;
        await ChangeAsync(booking, b =>
        {
            if (error.Kind is SupplierErrorKind.Timeout or SupplierErrorKind.Unknown)
            {
                // The request may have reached the supplier. Booking again could book twice, so ask the supplier instead.
                b.Status = HubBookingStatus.Unknown;
                b.Note = $"The answer was lost ({error.Kind}); the supplier is asked what it has before anything is retried.";
                logger.LogWarning("Booking {BookingId} at {Supplier} has an unknown outcome: {Message}", b.Id, b.Supplier, error.Message);
            }
            else
            {
                b.Status = HubBookingStatus.Failed;
                b.FailureReason = $"{error.Kind}: {error.Message}";
            }
        }).ConfigureAwait(false);
    }

    private async Task SettleAbsentAsync(BookingRecord booking, SupplierEntry entry)
    {
        var canResubmit = booking.Status == HubBookingStatus.Unknown
                          && entry.Descriptor.Capabilities.HasFlag(SupplierCapabilities.DuplicateDetection)
                          && booking.BookAttempts < options.Value.MaxBookAttempts;
        if (!canResubmit)
        {
            await ChangeAsync(booking, b =>
            {
                b.Status = HubBookingStatus.Failed;
                b.FailureReason = "The supplier holds no booking for this request.";
            }).ConfigureAwait(false);
            return;
        }

        await ChangeAsync(booking, b => b.Note = "The supplier holds no booking for this request; booking again with the same reference.").ConfigureAwait(false);
        await AttemptBookAsync(booking, Snapshot.Read<RoomOffer>(booking.OfferJson)).ConfigureAwait(false);
    }

    private async Task ApplyConfirmationAsync(BookingRecord booking, BookingConfirmation confirmation)
    {
        await ChangeAsync(booking, b =>
        {
            b.SupplierBookingRef = confirmation.SupplierBookingRef;
            switch (confirmation.Status)
            {
                case BookingStatus.Confirmed:
                    b.Status = HubBookingStatus.Confirmed;
                    b.Note = b.Note is null ? null : $"{b.Note} The supplier confirmed it.";
                    break;
                case BookingStatus.Pending:
                    b.Status = HubBookingStatus.Pending;
                    break;
                case BookingStatus.Cancelled:
                    b.Status = HubBookingStatus.Cancelled;
                    b.CancelStatus = nameof(BookingCancelStatus.AlreadyCancelled);
                    b.CancelledAt = time.GetUtcNow();
                    break;
                default:
                    b.Status = HubBookingStatus.Failed;
                    b.FailureReason = "The supplier reports the booking as failed.";
                    break;
            }
        }).ConfigureAwait(false);
    }

    private async Task ChangeAsync(BookingRecord booking, Action<BookingRecord> change)
    {
        change(booking);
        booking.UpdatedAt = time.GetUtcNow();
        await bookings.UpdateAsync(booking, CancellationToken.None).ConfigureAwait(false);
    }

    private BookingCreateOutcome AlreadyClaimed(BookingRecord existing, string hash) =>
        existing.PayloadHash == hash
            ? new BookingCreateOutcome(BookingCreateStatus.Replayed, existing, null, null)
            : new BookingCreateOutcome(BookingCreateStatus.KeyReused, null, null, "This idempotency key was used for a different request.");

    private async Task<BookingCreateOutcome> StaleQuoteAsync(QuoteRecord quote, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fresh = await quoteService.QuoteAsync(quote.Tenant, quote.OfferId, cancellationToken).ConfigureAwait(false);
        var status = quote.Status == QuoteStatus.Superseded ? BookingCreateStatus.PriceChanged : BookingCreateStatus.QuoteExpired;
        var detail = quote.Status switch
        {
            QuoteStatus.Superseded => "A newer quote replaced this one; book against the current quote.",
            QuoteStatus.Used => "This quote was already used by a booking.",
            _ => quote.ExpiresAt <= now ? "The quote has expired." : "The quote can no longer be used.",
        };
        return new BookingCreateOutcome(status, null, fresh.Quote, detail);
    }

    private static Money ToSelling(BookingRecord booking, Money supplierAmount) =>
        booking.SupplierAmount == 0m
            ? new Money(0m, booking.SellingCurrency)
            : new Money(Math.Round(supplierAmount.Amount * (booking.SellingAmount / booking.SupplierAmount), 2, MidpointRounding.AwayFromZero), booking.SellingCurrency);

    private static SupplierId Supplier(BookingRecord booking) => SupplierId.Parse(booking.Supplier);

    private static SupplierId Supplier(QuoteRecord quote) => SupplierId.Parse(quote.Supplier);

    private static string PayloadHash(CreateBookingCommand c) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{c.QuoteId}|{c.Amount:0.00}|{c.Currency}|{c.LeadGuestName.Trim()}|{c.PaymentMode}"))));
}

/// <summary>A fixed set of async locks that keys are hashed onto: operations on one booking run one at a time, with no per-key cleanup.</summary>
internal sealed class StripedLock(int stripes)
{
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, stripes).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        var gate = _gates[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_gates.Length)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
