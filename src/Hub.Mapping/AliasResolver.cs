using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

/// <summary>
/// Follows merge history from a retired canonical id to the id that survived. Chains are followed for at most
/// <see cref="MappingOptions.AliasMaxHops"/> hops with cycle detection; a cycle or an over-long chain leaves the id
/// unchanged. Lookups are cached (found aliases and "not an alias" for different durations).
/// The resolver fails open: if the alias store errors, the id is returned unchanged and the failure is logged and
/// counted, because losing a hotel from the results is worse than briefly showing it under a retired id.
/// </summary>
public sealed class AliasResolver
{
    public const string MeterName = "Hub.Mapping";
    public const string FailureCounterName = "hub.mapping.alias_lookup_failures";

    private const int MaxCacheEntries = 50_000;

    private readonly IAliasStore _store;
    private readonly MappingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AliasResolver> _logger;
    private readonly Counter<long> _failures;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public AliasResolver(
        IAliasStore store,
        IOptions<MappingOptions> options,
        TimeProvider time,
        IMeterFactory meterFactory,
        ILogger<AliasResolver> logger)
    {
        _store = store;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _failures = meterFactory.Create(MeterName).CreateCounter<long>(
            FailureCounterName, unit: "{lookup}", description: "Alias lookups that failed open.");
    }

    public async ValueTask<string> ResolveAsync(string canonicalId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(canonicalId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Resolved;
        }

        string resolved;
        try
        {
            resolved = await FollowAsync(canonicalId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failures.Add(1);
            _logger.LogWarning(ex, "Alias lookup for {CanonicalId} failed; returning the id unchanged.", canonicalId);
            return canonicalId;
        }

        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        var ttl = string.Equals(resolved, canonicalId, StringComparison.Ordinal) ? _options.AliasMissTtl : _options.AliasHitTtl;
        _cache[canonicalId] = new CacheEntry(resolved, now + ttl);
        return resolved;
    }

    /// <summary>Drops cached results; call after an alias is written so the change is visible immediately.</summary>
    public void InvalidateAll() => _cache.Clear();

    private async ValueTask<string> FollowAsync(string start, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var current = start;
        var hops = 0;

        while (true)
        {
            var survivor = await _store.GetSurvivorAsync(current, cancellationToken).ConfigureAwait(false);
            if (survivor is null)
            {
                return current;
            }

            if (!visited.Add(survivor))
            {
                _logger.LogWarning("Alias cycle detected starting at {CanonicalId}; leaving the id unchanged.", start);
                return start;
            }

            if (++hops > _options.AliasMaxHops)
            {
                _logger.LogWarning("Alias chain from {CanonicalId} is longer than {MaxHops} hops; leaving the id unchanged.", start, _options.AliasMaxHops);
                return start;
            }

            current = survivor;
        }
    }

    private readonly record struct CacheEntry(string Resolved, DateTimeOffset ExpiresAt);
}
