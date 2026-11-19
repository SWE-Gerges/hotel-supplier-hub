using System.Security.Cryptography;
using System.Text;
using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Core;

/// <summary>An offer as a search handed it out, with what is needed to address it at the supplier again.</summary>
public sealed record StoredOffer(
    string Tenant,
    RoomOffer Offer,
    SupplierSearchQuery Search,
    string HotelName,
    string? CanonicalId,
    DateTimeOffset StoredAt);

public sealed class OfferStoreOptions
{
    public const string SectionName = "Hub:Offers";

    /// <summary>How long an offer from a search can still be quoted.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Offers kept in memory; the oldest are dropped first.</summary>
    public int MaxOffers { get; set; } = 50_000;
}

internal sealed class OfferStoreOptionsValidator : IValidateOptions<OfferStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, OfferStoreOptions options) =>
        options.Retention > TimeSpan.Zero && options.MaxOffers >= 1
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Offers: Retention must be positive and MaxOffers at least 1.");
}

/// <summary>Where the offers of recent searches are kept so they can be quoted. Prices written back by a re-quote are visible to later reads.</summary>
public interface IOfferStore
{
    void Save(string tenant, string? canonicalId, string hotelName, RoomOffer offer, SupplierSearchQuery search);

    StoredOffer? Get(string tenant, string offerId);

    /// <summary>Replaces the price of a stored offer; null when the offer is no longer stored.</summary>
    StoredOffer? UpdatePrice(string tenant, string offerId, Money supplierPrice, Money sellingPrice);
}

/// <summary>Bounded in-memory offer store. Offers are lost on restart, after which they must be searched for again.</summary>
public sealed class InMemoryOfferStore(IOptions<OfferStoreOptions> options, TimeProvider time) : IOfferStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string OfferId), StoredOffer> _offers = [];
    private readonly Queue<(string Tenant, string OfferId)> _order = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _offers.Count;
            }
        }
    }

    public void Save(string tenant, string? canonicalId, string hotelName, RoomOffer offer, SupplierSearchQuery search)
    {
        var key = (tenant, offer.OfferId);
        var stored = new StoredOffer(tenant, offer, search, hotelName, canonicalId, time.GetUtcNow());
        lock (_gate)
        {
            if (!_offers.ContainsKey(key))
            {
                _order.Enqueue(key);
            }

            // The newest search wins: it is fresher than any earlier quote of the same offer.
            _offers[key] = stored;
            while (_offers.Count > options.Value.MaxOffers && _order.TryDequeue(out var oldest))
            {
                _offers.Remove(oldest);
            }
        }
    }

    public StoredOffer? Get(string tenant, string offerId)
    {
        lock (_gate)
        {
            if (!_offers.TryGetValue((tenant, offerId), out var stored))
            {
                return null;
            }

            if (stored.StoredAt + options.Value.Retention <= time.GetUtcNow())
            {
                _offers.Remove((tenant, offerId));
                return null;
            }

            return stored;
        }
    }

    public StoredOffer? UpdatePrice(string tenant, string offerId, Money supplierPrice, Money sellingPrice)
    {
        lock (_gate)
        {
            if (!_offers.TryGetValue((tenant, offerId), out var stored))
            {
                return null;
            }

            var updated = stored with { Offer = stored.Offer with { Price = supplierPrice, SellingPrice = sellingPrice } };
            _offers[(tenant, offerId)] = updated;
            return updated;
        }
    }
}

/// <summary>
/// Supplier offer ids say nothing about who is travelling or in which currency, so the hub adds a short digest of the
/// search context. The same search always yields the same id; a different party size or currency yields a different one.
/// </summary>
public static class OfferIds
{
    public static string Scope(string supplierOfferId, HotelSearchRequest request)
    {
        var context = string.Join(
            '|',
            request.Currency,
            string.Join(',', request.Rooms.Select(static r => $"{r.Adults}:{string.Join('-', r.ChildAges)}")));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context)))[..8];
        return $"{supplierOfferId}~{digest}";
    }
}
