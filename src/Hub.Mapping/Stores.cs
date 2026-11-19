using System.Globalization;
using Hub.Abstractions;

namespace Hub.Mapping;

/// <summary>One supplier hotel code that belongs to a canonical hotel.</summary>
public sealed record IdentityRecord(string CanonicalId, SupplierId Supplier, string SupplierCode);

/// <param name="CanonicalId">Attach to this canonical hotel; null mints a new one.</param>
public sealed record AddIdentityRequest(SupplierId Supplier, string SupplierCode, string? CanonicalId = null);

public enum AddIdentityStatus
{
    Created,

    /// <summary>The supplier code already belongs to the requested canonical hotel (or none was requested).</summary>
    AlreadyExists,

    /// <summary>The supplier code already belongs to a different canonical hotel.</summary>
    Conflict,

    CapExceeded,

    UnknownCanonicalId,
}

/// <param name="CanonicalId">The canonical hotel the record belongs to, when there is one.</param>
public sealed record AddIdentityResult(AddIdentityStatus Status, string? CanonicalId);

/// <summary>Canonical ids are opaque to callers; this is the one place that knows how they are formed.</summary>
public static class CanonicalIds
{
    public static string Format(long sequence) => string.Create(CultureInfo.InvariantCulture, $"H-{sequence:D6}");

    public static bool TryParse(string? value, out long sequence)
    {
        sequence = 0;
        return value is { Length: > 2 } && value.StartsWith("H-", StringComparison.Ordinal)
               && long.TryParse(value.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
               && sequence > 0;
    }
}

public interface IHotelIdentityStore
{
    /// <summary>Supplier code to canonical id, exactly as stored (retired ids are not followed). Unknown codes are absent.</summary>
    ValueTask<IReadOnlyDictionary<string, string>> FindCanonicalIdsAsync(
        SupplierId supplier, IReadOnlyCollection<string> supplierCodes, CancellationToken cancellationToken);

    /// <summary>Records of one canonical id in the order they were added.</summary>
    ValueTask<IReadOnlyList<IdentityRecord>> GetByCanonicalIdAsync(string canonicalId, CancellationToken cancellationToken);

    ValueTask<bool> CanonicalIdExistsAsync(string canonicalId, CancellationToken cancellationToken);

    ValueTask<AddIdentityResult> AddAsync(AddIdentityRequest request, CancellationToken cancellationToken);

    /// <summary>Throws when the backing store cannot be reached; used by readiness checks.</summary>
    ValueTask PingAsync(CancellationToken cancellationToken);
}

/// <summary>Merge history: a retired canonical id points at the id that survived the merge.</summary>
public interface IAliasStore
{
    ValueTask<string?> GetSurvivorAsync(string retiredId, CancellationToken cancellationToken);

    ValueTask SetAliasAsync(string retiredId, string survivorId, CancellationToken cancellationToken);

    /// <summary>Ids whose alias points directly at <paramref name="survivorId"/>.</summary>
    ValueTask<IReadOnlyList<string>> GetRetiredIdsAsync(string survivorId, CancellationToken cancellationToken);
}
