using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

/// <summary>Process-local store for tests and single-node demos. State is lost on restart.</summary>
public sealed class InMemoryHotelIdentityStore(IOptions<MappingOptions> options) : IHotelIdentityStore, IAliasStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(SupplierId Supplier, string Code), string> _byCode = [];
    private readonly Dictionary<string, List<IdentityRecord>> _byCanonical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private long _sequence;

    public ValueTask<IReadOnlyDictionary<string, string>> FindCanonicalIdsAsync(
        SupplierId supplier, IReadOnlyCollection<string> supplierCodes, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var code in supplierCodes)
            {
                if (_byCode.TryGetValue((supplier, code), out var canonicalId))
                {
                    found[code] = canonicalId;
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(found);
    }

    public ValueTask<IReadOnlyList<IdentityRecord>> GetByCanonicalIdAsync(string canonicalId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<IdentityRecord> records = _byCanonical.TryGetValue(canonicalId, out var list) ? list.ToArray() : [];
            return ValueTask.FromResult(records);
        }
    }

    public ValueTask<bool> CanonicalIdExistsAsync(string canonicalId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_byCanonical.ContainsKey(canonicalId));
        }
    }

    public ValueTask<AddIdentityResult> AddAsync(AddIdentityRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_byCode.TryGetValue((request.Supplier, request.SupplierCode), out var owner))
            {
                var same = request.CanonicalId is null || string.Equals(request.CanonicalId, owner, StringComparison.Ordinal);
                return ValueTask.FromResult(new AddIdentityResult(same ? AddIdentityStatus.AlreadyExists : AddIdentityStatus.Conflict, owner));
            }

            string canonicalId;
            if (request.CanonicalId is null)
            {
                canonicalId = CanonicalIds.Format(++_sequence);
                _byCanonical[canonicalId] = [];
            }
            else
            {
                canonicalId = request.CanonicalId;
                if (!_byCanonical.TryGetValue(canonicalId, out var existing))
                {
                    return ValueTask.FromResult(new AddIdentityResult(AddIdentityStatus.UnknownCanonicalId, null));
                }

                if (existing.Count >= options.Value.MaxRecordsPerCanonicalId)
                {
                    return ValueTask.FromResult(new AddIdentityResult(AddIdentityStatus.CapExceeded, canonicalId));
                }
            }

            _byCanonical[canonicalId].Add(new IdentityRecord(canonicalId, request.Supplier, request.SupplierCode));
            _byCode[(request.Supplier, request.SupplierCode)] = canonicalId;
            return ValueTask.FromResult(new AddIdentityResult(AddIdentityStatus.Created, canonicalId));
        }
    }

    public ValueTask PingAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<string?> GetSurvivorAsync(string retiredId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_aliases.GetValueOrDefault(retiredId));
        }
    }

    public ValueTask SetAliasAsync(string retiredId, string survivorId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _aliases[retiredId] = survivorId;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<string>> GetRetiredIdsAsync(string survivorId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<string> retired = _aliases
                .Where(pair => string.Equals(pair.Value, survivorId, StringComparison.Ordinal))
                .Select(static pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult(retired);
        }
    }
}
