namespace Hub.Abstractions;

public sealed record GeoPoint(double Latitude, double Longitude);

/// <summary>Points back to the record a supplier holds for a hotel.</summary>
public sealed record SupplierHotelRef(SupplierId Supplier, string Code);

/// <summary>Meal plan, normalised across suppliers. The supplier's original text is kept in <c>RawBoard</c>.</summary>
public enum BoardType
{
    RoomOnly,
    Breakfast,
    HalfBoard,
    FullBoard,
    AllInclusive,
    Unknown,
}

public enum PenaltyKind
{
    /// <summary>A share of the total price, 0-100.</summary>
    Percent,

    /// <summary>A number of nights' price.</summary>
    Nights,

    /// <summary>A fixed amount in <see cref="CancellationPenalty.Currency"/>.</summary>
    Fixed,
}

public sealed record CancellationPenalty(PenaltyKind Kind, decimal Value, string? Currency = null);

/// <summary>From <paramref name="From"/> on, cancelling costs <paramref name="Penalty"/>. Tiers are ordered by time and never get cheaper.</summary>
public sealed record CancellationTier(DateTimeOffset From, CancellationPenalty Penalty);

public sealed record RoomOffer
{
    public required string OfferId { get; init; }

    public required SupplierId Supplier { get; init; }

    public required string SupplierHotelCode { get; init; }

    public required string RoomName { get; init; }

    public required BoardType Board { get; init; }

    public required string RawBoard { get; init; }

    public required bool Refundable { get; init; }

    /// <summary>What the supplier charges.</summary>
    public required Money Price { get; init; }

    /// <summary>What the tenant sells it for (supplier price plus the tenant's markup).</summary>
    public required Money SellingPrice { get; init; }

    /// <summary>The normalised cancellation schedule, filled by the policy normaliser; empty when the policy is not known.</summary>
    public IReadOnlyList<CancellationTier> CancellationTiers { get; init; } = [];

    /// <summary>Last moment cancelling is free, when the policy has a free window.</summary>
    public DateTimeOffset? RefundableUntil { get; init; }

    /// <summary>False when the supplier's policy could not be understood; such an offer is treated as non-refundable.</summary>
    public bool PolicyKnown { get; init; }


    /// <summary>Supplier-specific details that do not belong in the canonical model, such as free-form policy text.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();

    /// <summary>Equal for near-identical rooms from different suppliers; assigned by the hub when merging.</summary>
    public string? GroupKey { get; init; }
}

public sealed record Hotel
{
    /// <summary>Null until the mapping layer recognises the supplier's code.</summary>
    public string? CanonicalId { get; init; }

    public required string Name { get; init; }

    public int Stars { get; init; }

    public GeoPoint? Location { get; init; }

    public string Address { get; init; } = string.Empty;

    public required IReadOnlyList<RoomOffer> Offers { get; init; }

    public required IReadOnlyList<SupplierHotelRef> Sources { get; init; }

    /// <summary>A supplier's localized name and address for this place. The merger applies it only if it is the same place.</summary>
    public LocalizedOverlay? Overlay { get; init; }
}

public sealed record LocalizedOverlay(string Locale, string Name, string? Address, GeoPoint? Location);
