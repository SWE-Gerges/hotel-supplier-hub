using System.Text.Json.Serialization;
using Hub.Abstractions;

namespace Hub.Core;

/// <summary>An event in the life of one search run, in the order the run produced it.</summary>
public abstract record SearchUpdate(string SearchId)
{
    /// <summary>The name used for this update on the wire, for instance as the server-sent event name.</summary>
    [JsonIgnore]
    public abstract string EventName { get; }
}

public sealed record SupplierStarted(string SearchId, SupplierId Supplier) : SearchUpdate(SearchId)
{
    [JsonIgnore]
    public override string EventName => "supplierStarted";
}

/// <summary>Hotels seen for the first time in this run.</summary>
public sealed record HotelsAdded(string SearchId, IReadOnlyList<Hotel> Hotels) : SearchUpdate(SearchId)
{
    [JsonIgnore]
    public override string EventName => "hotelsAdded";
}

/// <summary>An already reported hotel gained offers or sources, or its cheapest price moved.</summary>
public sealed record HotelUpdated(string SearchId, Hotel Hotel, Money? PreviousCheapest, Money? NewCheapest) : SearchUpdate(SearchId)
{
    [JsonIgnore]
    public override string EventName => "hotelUpdated";
}

public sealed record SupplierFailed(string SearchId, SupplierId Supplier, SupplierError Error, long ElapsedMs) : SearchUpdate(SearchId)
{
    [JsonIgnore]
    public override string EventName => "supplierFailed";
}

public sealed record SupplierCompleted(string SearchId, SupplierId Supplier, int HotelCount, long ElapsedMs) : SearchUpdate(SearchId)
{
    [JsonIgnore]
    public override string EventName => "supplierCompleted";
}

public sealed record Completed(string SearchId, SearchSummary Summary) : SearchUpdate(SearchId)
{
    [JsonIgnore]
    public override string EventName => "completed";
}

public enum SearchStatus
{
    /// <summary>Every queried supplier answered (also the result when no supplier was eligible).</summary>
    Completed,

    /// <summary>Some suppliers answered and some failed or timed out.</summary>
    Partial,

    /// <summary>Suppliers were queried and none answered.</summary>
    Failed,

    /// <summary>The run was cancelled before it finished.</summary>
    Cancelled,
}

public enum SupplierOutcomeStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record SupplierOutcome(
    SupplierId Supplier,
    SupplierOutcomeStatus Status,
    int HotelCount,
    long ElapsedMs,
    SupplierError? Error);

public sealed record SearchSummary(
    SearchStatus Status,
    int HotelCount,
    int OfferCount,
    long ElapsedMs,
    IReadOnlyList<SupplierOutcome> Suppliers);

/// <summary>A finished (or deadline-limited) search collected into one response.</summary>
public sealed record SearchResult(string SearchId, SearchSummary Summary, IReadOnlyList<Hotel> Hotels);
