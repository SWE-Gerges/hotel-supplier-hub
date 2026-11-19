using Hub.Abstractions;

namespace Hub.Booking;

public enum QuoteStatus
{
    /// <summary>The latest quote of its offer; a booking may still use it.</summary>
    Active,

    /// <summary>A newer quote of the same offer replaced it, so the amount it showed is no longer the amount to charge.</summary>
    Superseded,

    /// <summary>A booking was made against it.</summary>
    Used,
}

/// <summary>
/// What the client was shown for one offer at one moment: the selling amount, until when it holds, and the policy. A
/// booking is made against exactly one quote and only for exactly this amount.
/// </summary>
public sealed class QuoteRecord
{
    public string Id { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    public string OfferId { get; set; } = string.Empty;

    public string Supplier { get; set; } = string.Empty;

    public decimal SellingAmount { get; set; }

    public string SellingCurrency { get; set; } = string.Empty;

    public decimal SupplierAmount { get; set; }

    public string SupplierCurrency { get; set; } = string.Empty;

    public QuoteStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public bool Refundable { get; set; }

    public bool PolicyKnown { get; set; }

    public DateTimeOffset? FreeCancellationUntil { get; set; }

    /// <summary>The offer as the supplier needs to see it again, so booking works after a restart.</summary>
    public string OfferJson { get; set; } = string.Empty;

    public string SearchJson { get; set; } = string.Empty;

    public Money Selling => new(SellingAmount, SellingCurrency);

    public Money SupplierPrice => new(SupplierAmount, SupplierCurrency);
}

public sealed record PriceChange(Money From, Money To);

public sealed record QuoteView(
    string QuoteId,
    string OfferId,
    DateTimeOffset ExpiresAt,
    Money SellingPrice,
    PriceChange? PriceChanged,
    bool Refundable,
    DateTimeOffset? FreeCancellationUntil);

public enum QuoteFailure
{
    /// <summary>The offer is not (or no longer) known to the hub; search again.</summary>
    OfferNotFound,

    /// <summary>The supplier no longer sells the offer.</summary>
    SoldOut,

    /// <summary>The supplier session behind the offer expired; search again.</summary>
    SessionExpired,

    /// <summary>The supplier cannot re-price offers at all.</summary>
    Unsupported,

    /// <summary>The supplier call failed; the offer may be fine and trying again may work.</summary>
    SupplierFailed,

    /// <summary>No exchange rate converts the supplier's currency to the offer's.</summary>
    NotPriceable,
}

public sealed record QuoteOutcome(QuoteView? Quote, QuoteFailure? Failure, string? Detail)
{
    public static QuoteOutcome Ok(QuoteView quote) => new(quote, null, null);

    public static QuoteOutcome Fail(QuoteFailure failure, string detail) => new(null, failure, detail);
}

public sealed class QuoteOptions
{
    public const string SectionName = "Hub:Quotes";

    /// <summary>How long a quote can be booked, unless the supplier holds its price for less.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(10);
}
