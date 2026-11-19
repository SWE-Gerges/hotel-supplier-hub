using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

// supplier-a's booking side: synchronous JSON calls. Prebook returns the current price, booking confirms at once, a
// repeated client reference returns the booking that already exists, and cancelling answers with a result code plus a
// free-text fee ("Cancellation fee: 120,50 EUR").

internal sealed record ARoomKey(string HotelCode, string RoomCode, string BoardCode, Stay Stay);

internal sealed record APreBookResponse(AMoney? Net, AMoney? Taxes, string? ValidUntil, AError? Error);

internal sealed record ABookRequest(string ClientRef, ARoomKey Room, string ExpectedTotal, string Guest);

internal sealed record ABooking(string BookingId, string ClientRef, string State, string Total, string Currency);

internal sealed record ABookResponse(ABooking? Booking, AError? Error = null);

internal sealed record ACancelResponse(string? Result, string? Fee, string? Refund, string? Currency, AError? Error);

/// <summary>supplier-a's mapping of wire room fields into <see cref="RoomOffer.Extra"/>.</summary>
internal sealed class SupplierARoomExtraMapper : IRoomExtraMapper<ARoom>
{
    public SupplierId Supplier => FakeSupplierIds.SupplierA;

    public Type WireRoomType => typeof(ARoom);

    public void Map(ARoom room, RoomExtraContext context, IDictionary<string, string> extra)
    {
        extra["cancellationText"] = room.Rate.CancellationPolicy ?? string.Empty;
        extra["roomCode"] = room.RoomCode;
        extra["boardCode"] = room.BoardCode;
    }
}

internal sealed partial class SupplierAServer
{
    private static readonly TimeSpan PriceHold = TimeSpan.FromMinutes(15);

    public async Task<string> PreBookAsync(ARoomKey room, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new APreBookResponse(null, null, null, ErrorFor(kind)), decision);
        }

        if (FindOffer(room) is not { } offer)
        {
            return Serialize(new APreBookResponse(null, null, null, new AError("SOLD_OUT", "The room is no longer available.")), decision);
        }

        var total = FakeBookingSupport.Drifted(offer.Total, faults.Current);
        var taxes = Math.Round(total * 0.1m, 2, MidpointRounding.AwayFromZero);
        return Serialize(
            new APreBookResponse(
                new AMoney(FakeBookingSupport.Money2(total - taxes), room.Stay.Currency),
                new AMoney(FakeBookingSupport.Money2(taxes).Replace('.', ','), room.Stay.Currency),
                (time.GetUtcNow() + PriceHold).ToString("O", CultureInfo.InvariantCulture),
                null),
            decision);
    }

    public async Task<string> BookAsync(ABookRequest request, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new ABookResponse(null, ErrorFor(kind)), decision);
        }

        if (ledger.Find(null, request.ClientRef) is { } existing)
        {
            return Serialize(new ABookResponse(ToWire(existing)), decision);
        }

        if (FindOffer(request.Room) is not { } offer)
        {
            return Serialize(new ABookResponse(null, new AError("SOLD_OUT", "The room is no longer available.")), decision);
        }

        var current = FakeBookingSupport.Drifted(offer.Total, faults.Current);
        if (!TolerantDecimal.TryParse(request.ExpectedTotal, out var expected) || expected != current)
        {
            return Serialize(new ABookResponse(null, new AError("PRICE_CHANGED", "The price differs from the one expected.")), decision);
        }

        var now = time.GetUtcNow();
        var (booking, duplicate) = ledger.GetOrCreate(request.ClientRef, reference => FakeBookingSupport.NewBooking(
            reference, request.ClientRef, $"{request.Room.HotelCode}.{request.Room.RoomCode}.{request.Room.BoardCode}", request.Room.HotelCode,
            offer, current, request.Room.Stay, now, TimeSpan.Zero, endsFailed: false));
        if (!duplicate && faults.RollAcceptedButLost())
        {
            return Serialize(new ABookResponse(null, new AError("UPSTREAM_TIMEOUT", "An upstream call timed out.")), decision);
        }

        return Serialize(new ABookResponse(ToWire(booking)), decision);
    }

    public async Task<string> CancelAsync(string bookingId, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new ACancelResponse(null, null, null, null, ErrorFor(kind)), decision);
        }

        if (ledger.Find(bookingId, null) is not { } booking)
        {
            return Serialize(new ACancelResponse(null, null, null, null, new AError("BOOKING_NOT_FOUND", "No such booking.")), decision);
        }

        lock (booking)
        {
            if (booking.Cancelled)
            {
                return Serialize(new ACancelResponse("ALREADY_CANCELLED", null, null, booking.Currency, null), decision);
            }

            var fee = booking.FeeAt(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
            booking.Cancelled = true;
            var response = fee > 0
                ? new ACancelResponse(
                    "CANCELLED_WITH_FEE",
                    $"Cancellation fee: {FakeBookingSupport.Money2(fee).Replace('.', ',')} {booking.Currency}",
                    FakeBookingSupport.Money2(booking.Total - fee),
                    booking.Currency,
                    null)
                : new ACancelResponse("CANCELLED", null, FakeBookingSupport.Money2(booking.Total), booking.Currency, null);
            return Serialize(response, decision);
        }
    }

    public async Task<string> RetrieveAsync(string? bookingId, string? clientRef, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new ABookResponse(null, ErrorFor(kind)), decision);
        }

        return ledger.Find(bookingId, clientRef) is { } booking
            ? Serialize(new ABookResponse(ToWire(booking)), decision)
            : Serialize(new ABookResponse(null, new AError("BOOKING_NOT_FOUND", "No such booking.")), decision);
    }

    private PhysicalOffer? FindOffer(ARoomKey room) =>
        FakeBookingSupport.FindOffer(catalog, FakeSupplierIds.SupplierA, room.HotelCode, room.RoomCode, SupplierANormalizer.ParseBoard(room.BoardCode), room.Stay);

    private ABooking ToWire(LedgerBooking booking) => new(
        booking.BookingRef,
        booking.ClientRef,
        booking.StateAt(time.GetUtcNow()) switch
        {
            LedgerState.Cancelled => "CANCELLED",
            LedgerState.Failed => "FAILED",
            LedgerState.Pending => "PENDING",
            _ => "CONFIRMED",
        },
        FakeBookingSupport.Money2(booking.Total),
        booking.Currency);
}

internal sealed partial class SupplierAClient
{
    public async ValueTask<Result<IReadOnlyList<RoomOffer>>> GetRoomsAsync(RoomAvailabilityQuery query, CancellationToken cancellationToken)
    {
        if (FakeSupplierSupport.ValidateQuery(query.Search) is { } invalid)
        {
            return Result.Fail<IReadOnlyList<RoomOffer>>(invalid);
        }

        return await FakeSupplierSupport.GuardAsync<IReadOnlyList<RoomOffer>>(async () =>
        {
            var json = await server.GetRatesAsync(new ARatesRequest([query.SupplierHotelCode], FakeSupplierSupport.ToStay(query.Search.Request)), cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<AHotelsResponse>(json, SupplierAJson.Options) ?? throw new JsonException("Empty response.");
            var hotels = normalizer.Normalize(response, query.Search);
            return hotels.IsFailure
                ? Result.Fail<IReadOnlyList<RoomOffer>>(hotels.Error)
                : Result.Ok<IReadOnlyList<RoomOffer>>(hotels.Value.SelectMany(static h => h.Offers).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<PreBookQuote>> PreBookAsync(PreBookRequest request, CancellationToken cancellationToken)
    {
        if (RoomKeyOf(request.Offer) is not { } room)
        {
            return Result.Fail<PreBookQuote>(SupplierError.InvalidRequest("The offer carries no room reference for supplier-a."));
        }

        return await FakeSupplierSupport.GuardAsync(async () =>
        {
            var json = await server.PreBookAsync(room, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<APreBookResponse>(json, SupplierAJson.Options) ?? throw new JsonException("Empty response.");
            if (response.Error is { } error)
            {
                return Result.Fail<PreBookQuote>(SupplierANormalizer.MapError(error));
            }

            if (response.Net is not { } net || !TolerantDecimal.TryParse(net.Amount, out var netAmount)
                || !TolerantDecimal.TryParse(response.Taxes?.Amount ?? "0", out var taxes))
            {
                return Result.Fail<PreBookQuote>(SupplierError.Unknown("The prebook answer carried no readable price."));
            }

            DateTimeOffset? validUntil = DateTimeOffset.TryParse(response.ValidUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            return Result.Ok(new PreBookQuote(request.Offer.Offer.OfferId, new Money(netAmount + taxes, net.Currency.ToUpperInvariant()), validUntil));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<BookingConfirmation>> BookAsync(BookRequest request, CancellationToken cancellationToken)
    {
        if (RoomKeyOf(request.Offer) is not { } room)
        {
            return Result.Fail<BookingConfirmation>(SupplierError.InvalidRequest("The offer carries no room reference for supplier-a."));
        }

        return await FakeSupplierSupport.GuardAsync(async () =>
        {
            var wire = new ABookRequest(request.SupplierReference, room, FakeBookingSupport.Money2(request.ExpectedPrice.Amount), request.LeadGuestName);
            var json = await server.BookAsync(wire, cancellationToken).ConfigureAwait(false);
            return ReadBooking(json);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<CancellationOutcome>> CancelAsync(CancelRequest request, CancellationToken cancellationToken) =>
        await FakeSupplierSupport.GuardAsync(async () =>
        {
            var json = await server.CancelAsync(request.SupplierBookingRef, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<ACancelResponse>(json, SupplierAJson.Options) ?? throw new JsonException("Empty response.");
            if (response.Error is { } error)
            {
                return Result.Fail<CancellationOutcome>(SupplierANormalizer.MapError(error));
            }

            var currency = (response.Currency ?? "EUR").ToUpperInvariant();
            Money? refund = TolerantDecimal.TryParse(response.Refund, out var refundAmount) ? new Money(refundAmount, currency) : null;
            switch (response.Result)
            {
                case "CANCELLED":
                    return Result.Ok(new CancellationOutcome(request.SupplierBookingRef, SupplierCancelStatus.Cancelled, null, refund));
                case "ALREADY_CANCELLED":
                    return Result.Ok(new CancellationOutcome(request.SupplierBookingRef, SupplierCancelStatus.AlreadyCancelled, null, null));
                case "CANCELLED_WITH_FEE":
                    var fee = FeeText().Match(response.Fee ?? string.Empty);
                    if (!fee.Success || !TolerantDecimal.TryParse(fee.Groups["amount"].Value, out var feeAmount))
                    {
                        return Result.Fail<CancellationOutcome>(SupplierError.Unknown("The cancellation fee could not be read.", FakeSupplierSupport.Excerpt(response.Fee)));
                    }

                    return Result.Ok(new CancellationOutcome(request.SupplierBookingRef, SupplierCancelStatus.PenaltyApplied, new Money(feeAmount, fee.Groups["currency"].Value), refund));
                default:
                    return Result.Fail<CancellationOutcome>(SupplierError.Unknown("Unrecognised cancellation result.", response.Result));
            }
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<Result<BookingConfirmation>> RetrieveAsync(RetrieveRequest request, CancellationToken cancellationToken) =>
        await FakeSupplierSupport.GuardAsync(async () =>
        {
            var json = await server.RetrieveAsync(request.SupplierBookingRef, request.SupplierReference, cancellationToken).ConfigureAwait(false);
            return ReadBooking(json);
        }, cancellationToken).ConfigureAwait(false);

    private static Result<BookingConfirmation> ReadBooking(string json)
    {
        var response = JsonSerializer.Deserialize<ABookResponse>(json, SupplierAJson.Options) ?? throw new JsonException("Empty response.");
        if (response.Error is { } error)
        {
            return Result.Fail<BookingConfirmation>(SupplierANormalizer.MapError(error));
        }

        if (response.Booking is not { } booking || !TolerantDecimal.TryParse(booking.Total, out var total))
        {
            return Result.Fail<BookingConfirmation>(SupplierError.Unknown("The booking answer could not be read."));
        }

        var status = booking.State switch
        {
            "CONFIRMED" => BookingStatus.Confirmed,
            "PENDING" => BookingStatus.Pending,
            "CANCELLED" => BookingStatus.Cancelled,
            _ => BookingStatus.Failed,
        };
        return Result.Ok(new BookingConfirmation(booking.BookingId, status, new Money(total, booking.Currency.ToUpperInvariant()), booking.ClientRef));
    }

    private static ARoomKey? RoomKeyOf(OfferRef offer) =>
        FakeBookingSupport.Extra(offer.Offer, "roomCode") is { } room && FakeBookingSupport.Extra(offer.Offer, "boardCode") is { } board
            ? new ARoomKey(offer.Offer.SupplierHotelCode, room, board, FakeSupplierSupport.ToStay(offer.Search.Request))
            : null;

    [GeneratedRegex(@"Cancellation fee:\s*(?<amount>[\d.,]+)\s*(?<currency>[A-Z]{3})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex FeeText();
}
