using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

public enum SetAliasStatus
{
    Set,
    SelfAlias,
    UnknownCanonicalId,
    WouldCreateCycle,
}

/// <param name="RequestedId">The id that was asked for.</param>
/// <param name="CanonicalId">The id after merge history is applied; differs from <paramref name="RequestedId"/> for retired ids.</param>
public sealed record CanonicalHotelView(
    string RequestedId,
    string CanonicalId,
    IReadOnlyList<IdentityRecord> Sources,
    IReadOnlyList<string> RetiredIds);

/// <summary>Operator-facing operations on identities and aliases, with the validation the raw stores leave out.</summary>
public sealed class HotelMappingService(
    IHotelIdentityStore identities,
    IAliasStore aliases,
    AliasResolver resolver,
    SupplierCodeMap codeMap,
    IOptions<MappingOptions> options)
{
    public ValueTask<AddIdentityResult> AddIdentityAsync(AddIdentityRequest request, CancellationToken cancellationToken) =>
        identities.AddAsync(request, cancellationToken);

    public async ValueTask<SetAliasStatus> SetAliasAsync(string retiredId, string survivorId, CancellationToken cancellationToken)
    {
        if (string.Equals(retiredId, survivorId, StringComparison.Ordinal))
        {
            return SetAliasStatus.SelfAlias;
        }

        if (!await identities.CanonicalIdExistsAsync(retiredId, cancellationToken).ConfigureAwait(false)
            || !await identities.CanonicalIdExistsAsync(survivorId, cancellationToken).ConfigureAwait(false))
        {
            return SetAliasStatus.UnknownCanonicalId;
        }

        // An alias that leads back to the retired id would make the lookup loop.
        var current = survivorId;
        for (var hop = 0; hop <= options.Value.AliasMaxHops; hop++)
        {
            var next = await aliases.GetSurvivorAsync(current, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            if (string.Equals(next, retiredId, StringComparison.Ordinal))
            {
                return SetAliasStatus.WouldCreateCycle;
            }

            current = next;
        }

        await aliases.SetAliasAsync(retiredId, survivorId, cancellationToken).ConfigureAwait(false);
        resolver.InvalidateAll();
        return SetAliasStatus.Set;
    }

    /// <summary>The hotel behind a canonical id (following merge history), or null when the id is unknown.</summary>
    public async ValueTask<CanonicalHotelView?> GetHotelAsync(string canonicalId, CancellationToken cancellationToken)
    {
        if (!await identities.CanonicalIdExistsAsync(canonicalId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var resolved = await resolver.ResolveAsync(canonicalId, cancellationToken).ConfigureAwait(false);
        var sources = await codeMap.GetRecordsAsync(resolved, cancellationToken).ConfigureAwait(false);
        var retired = await aliases.GetRetiredIdsAsync(resolved, cancellationToken).ConfigureAwait(false);
        return new CanonicalHotelView(canonicalId, resolved, sources, retired);
    }
}
