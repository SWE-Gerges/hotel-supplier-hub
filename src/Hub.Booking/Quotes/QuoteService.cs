using System.Text.Json;
using Hub.Abstractions;
using Hub.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hub.Booking;

/// <summary>JSON used for the offer and search snapshots kept with quotes and bookings.</summary>
internal static class Snapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException("The stored snapshot is empty.");
}

/// <summary>
/// Re-prices an offer with its supplier and records what the client is shown. A price that moved is written back to the
/// stored offer, so later reads show the new price, and the quote says what it was and what it is now.
/// </summary>
public sealed class QuoteService(
    IOfferStore offers,
    ISupplierRegistry registry,
    ISupplierGateway gateway,
    IPricingService pricing,
    IQuoteStore quotes,
    IOptions<QuoteOptions> options,
    TimeProvider time,
    ILogger<QuoteService> logger)
{
    public async Task<QuoteOutcome> QuoteAsync(string tenant, string offerId, CancellationToken cancellationToken)
    {
        if (offers.Get(tenant, offerId) is not { } stored)
        {
            return QuoteOutcome.Fail(QuoteFailure.OfferNotFound, "The offer is unknown or no longer kept; search again.");
        }

        var supplier = stored.Offer.Supplier;
        if (!registry.TryGet(supplier, out var entry))
        {
            return QuoteOutcome.Fail(QuoteFailure.Unsupported, $"Supplier '{supplier}' is not registered.");
        }

        var fresh = await ReadSupplierPriceAsync(entry, stored, cancellationToken).ConfigureAwait(false);
        if (fresh.Failure is { } failure)
        {
            return failure;
        }

        var supplierPrice = fresh.Price!.Value;
        var validUntil = fresh.ValidUntil;
        var current = stored.Offer.SellingPrice;
        if (pricing.ToSellingPrice(tenant, supplier, supplierPrice, current.Currency) is not { } selling)
        {
            return QuoteOutcome.Fail(QuoteFailure.NotPriceable, $"No exchange rate converts {supplierPrice.Currency} to {current.Currency}.");
        }

        var updated = stored.Offer with { Price = supplierPrice, SellingPrice = selling };
        PriceChange? changed = selling != current ? new PriceChange(current, selling) : null;
        if (changed is not null || supplierPrice != stored.Offer.Price)
        {
            offers.UpdatePrice(tenant, offerId, supplierPrice, selling);
            logger.LogInformation("Offer {OfferId} re-priced from {From} to {To} at {Supplier}.", offerId, current, selling, supplier);
        }

        var now = time.GetUtcNow();
        var expires = now + options.Value.Lifetime;
        if (validUntil is { } held && held < expires)
        {
            expires = held;
        }

        var quote = new QuoteRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Tenant = tenant,
            OfferId = offerId,
            Supplier = supplier.Value,
            SellingAmount = selling.Amount,
            SellingCurrency = selling.Currency,
            SupplierAmount = supplierPrice.Amount,
            SupplierCurrency = supplierPrice.Currency,
            Status = QuoteStatus.Active,
            CreatedAt = now,
            ExpiresAt = expires,
            Refundable = updated.Refundable && updated.PolicyKnown,
            PolicyKnown = updated.PolicyKnown,
            FreeCancellationUntil = updated.RefundableUntil,
            OfferJson = Snapshot.Write(updated),
            SearchJson = Snapshot.Write(stored.Search),
        };
        await quotes.AddAsync(quote, cancellationToken).ConfigureAwait(false);
        return QuoteOutcome.Ok(ToView(quote, changed));
    }

    public async Task<QuoteRecord?> GetAsync(string tenant, string quoteId, CancellationToken cancellationToken) =>
        await quotes.GetAsync(tenant, quoteId, cancellationToken).ConfigureAwait(false);

    internal static QuoteView ToView(QuoteRecord quote, PriceChange? changed) => new(
        quote.Id, quote.OfferId, quote.ExpiresAt, quote.Selling, changed, quote.Refundable, quote.FreeCancellationUntil);

    private sealed record SupplierPriceRead(Money? Price, DateTimeOffset? ValidUntil, QuoteOutcome? Failure);

    private async Task<SupplierPriceRead> ReadSupplierPriceAsync(
        SupplierEntry entry, StoredOffer stored, CancellationToken cancellationToken)
    {
        var supplier = stored.Offer.Supplier;
        var offerRef = new OfferRef(stored.Offer, stored.Search);
        if (entry.PreBook is not null)
        {
            var result = await gateway.CallAsync(
                supplier, SupplierOperation.PreBook, (e, ct) => e.PreBook!.PreBookAsync(new PreBookRequest(offerRef), ct), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? new SupplierPriceRead(result.Value.Price, result.Value.ValidUntil, null)
                : new SupplierPriceRead(null, null, MapFailure(result.Error));
        }

        if (entry.Availability is not null)
        {
            var result = await gateway.CallAsync(
                supplier,
                SupplierOperation.Availability,
                (e, ct) => e.Availability!.GetRoomsAsync(new RoomAvailabilityQuery(stored.Search, stored.Offer.SupplierHotelCode), ct),
                cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return new SupplierPriceRead(null, null, MapFailure(result.Error));
            }

            var match = result.Value.FirstOrDefault(o => OfferIds.Scope(o.OfferId, stored.Search.Request) == stored.Offer.OfferId);
            return match is null
                ? new SupplierPriceRead(null, null, QuoteOutcome.Fail(QuoteFailure.SoldOut, "The supplier no longer lists this room."))
                : new SupplierPriceRead(match.Price, null, null);
        }

        return new SupplierPriceRead(null, null, QuoteOutcome.Fail(QuoteFailure.Unsupported, $"Supplier '{supplier}' can neither prebook nor list rooms, so offers cannot be re-priced."));
    }

    private static QuoteOutcome MapFailure(SupplierError error) => error.Kind switch
    {
        SupplierErrorKind.SoldOut or SupplierErrorKind.NotFound => QuoteOutcome.Fail(QuoteFailure.SoldOut, "The supplier no longer sells this room."),
        SupplierErrorKind.SessionExpired => QuoteOutcome.Fail(QuoteFailure.SessionExpired, "The supplier session behind this offer has expired; search again."),
        SupplierErrorKind.Unsupported => QuoteOutcome.Fail(QuoteFailure.Unsupported, error.Message),
        _ => QuoteOutcome.Fail(QuoteFailure.SupplierFailed, error.Message),
    };
}
