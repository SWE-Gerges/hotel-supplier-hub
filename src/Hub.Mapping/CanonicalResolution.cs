using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

/// <summary>
/// Canonical hotel id to the supplier-specific code to send to each supplier. Suppliers differ (numeric ids,
/// alphanumeric codes), so the codes live in one dictionary keyed by supplier instead of a column per supplier.
/// Records of retired ids that alias to the hotel are included.
/// </summary>
public sealed class SupplierCodeMap(IHotelIdentityStore identities, IAliasStore aliases, AliasResolver resolver, IOptions<MappingOptions> options)
{
    public async ValueTask<IReadOnlyDictionary<SupplierId, string>> GetCodesAsync(string canonicalId, CancellationToken cancellationToken)
    {
        var codes = new Dictionary<SupplierId, string>();
        foreach (var record in await GetRecordsAsync(canonicalId, cancellationToken).ConfigureAwait(false))
        {
            codes.TryAdd(record.Supplier, record.SupplierCode);
        }

        return codes;
    }

    /// <summary>Every supplier record that belongs to the hotel once merge history is applied, survivor's records first.</summary>
    public async ValueTask<IReadOnlyList<IdentityRecord>> GetRecordsAsync(string canonicalId, CancellationToken cancellationToken)
    {
        var survivor = await resolver.ResolveAsync(canonicalId, cancellationToken).ConfigureAwait(false);

        var records = new List<IdentityRecord>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { survivor };
        var frontier = new List<string> { survivor };

        for (var depth = 0; frontier.Count > 0 && depth <= options.Value.AliasMaxHops; depth++)
        {
            var next = new List<string>();
            foreach (var id in frontier)
            {
                records.AddRange(await identities.GetByCanonicalIdAsync(id, cancellationToken).ConfigureAwait(false));
                foreach (var retired in await aliases.GetRetiredIdsAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    if (visited.Add(retired))
                    {
                        next.Add(retired);
                    }
                }
            }

            frontier = next;
        }

        return records;
    }
}

public sealed class CanonicalIdResolver(IHotelIdentityStore identities, AliasResolver aliases, SupplierCodeMap codeMap) : ICanonicalIdResolver
{
    public async ValueTask<IReadOnlyDictionary<string, string>> ResolveHotelsAsync(
        SupplierId supplier, IReadOnlyCollection<string> supplierCodes, CancellationToken cancellationToken)
    {
        var stored = await identities.FindCanonicalIdsAsync(supplier, supplierCodes, cancellationToken).ConfigureAwait(false);
        var resolved = new Dictionary<string, string>(stored.Count, StringComparer.Ordinal);
        foreach (var (code, canonicalId) in stored)
        {
            resolved[code] = await aliases.ResolveAsync(canonicalId, cancellationToken).ConfigureAwait(false);
        }

        return resolved;
    }

    public async ValueTask<string?> ResolveHotelAsync(SupplierId supplier, string supplierCode, CancellationToken cancellationToken)
    {
        var resolved = await ResolveHotelsAsync(supplier, [supplierCode], cancellationToken).ConfigureAwait(false);
        return resolved.GetValueOrDefault(supplierCode);
    }

    public async ValueTask<string?> GetSupplierCodeAsync(string canonicalId, SupplierId supplier, CancellationToken cancellationToken)
    {
        var codes = await codeMap.GetCodesAsync(canonicalId, cancellationToken).ConfigureAwait(false);
        return codes.GetValueOrDefault(supplier);
    }
}
