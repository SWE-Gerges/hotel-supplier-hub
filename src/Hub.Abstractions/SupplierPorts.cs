namespace Hub.Abstractions;

[Flags]
public enum SupplierCapabilities
{
    None = 0,
    Search = 1,
    RoomAvailability = 2,
    PreBook = 4,
    Book = 8,
    Cancel = 16,
    Retrieve = 32,

    /// <summary>The supplier refuses to book the same client reference twice and returns the booking it already made.</summary>
    DuplicateDetection = 64,

    /// <summary>The supplier lists its cities and countries, so the crosswalk can map them to canonical cities.</summary>
    CityDirectory = 128,
}

/// <summary>How a supplier's search protocol works; informational for operators and the descriptor listing.</summary>
public enum SearchStyle
{
    PagedSync,
    AsyncPolling,
    SessionTwoStep,
}

public interface ISupplierDescriptor
{
    SupplierId SupplierId { get; }

    SupplierCapabilities Capabilities { get; }

    SearchStyle SearchStyle { get; }

    /// <summary>Overall time budget for one search call; used unless resilience options override it.</summary>
    TimeSpan DefaultTimeout { get; }

    /// <summary>Maximum in-flight calls the supplier tolerates; seeds the bulkhead size.</summary>
    int MaxConcurrency { get; }
}

/// <summary>
/// Streams hotel batches as the supplier produces them. A failure item ends the stream, and unexpected
/// exceptions must be converted to <see cref="SupplierErrorKind.Unknown"/> failures. Cancellation surfaces
/// as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IHotelSearch
{
    IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> SearchAsync(SupplierSearchQuery query, CancellationToken cancellationToken);
}

/// <summary>Each supplier owns the translation from its own wire model to the canonical model.</summary>
public interface ISupplierResponseNormalizer<in TResponse>
{
    Result<IReadOnlyList<Hotel>> Normalize(TResponse response, SupplierSearchQuery query);
}

/// <summary>What a room-extra mapper may need besides the wire room itself.</summary>
public sealed record RoomExtraContext(SupplierId Supplier, string HotelCode, string? SessionId = null);

/// <summary>Non-generic face of <see cref="IRoomExtraMapper{TWireRoom}"/>, so mappers can be registered together.</summary>
public interface IRoomExtraMapper
{
    SupplierId Supplier { get; }

    Type WireRoomType { get; }
}

/// <summary>
/// Strategy owned by one supplier: copies the wire fields that do not belong in the canonical room (raw policy text,
/// rate keys, session ids) into <see cref="RoomOffer.Extra"/>. A supplier without a mapper contributes no extras.
/// </summary>
public interface IRoomExtraMapper<in TWireRoom> : IRoomExtraMapper
{
    void Map(TWireRoom room, RoomExtraContext context, IDictionary<string, string> extra);
}
