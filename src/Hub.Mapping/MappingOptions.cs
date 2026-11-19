using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

public sealed class MappingOptions
{
    public const string SectionName = "Hub:Mapping";

    /// <summary>Most supplier records one canonical hotel may own.</summary>
    public int MaxRecordsPerCanonicalId { get; set; } = 50;

    /// <summary>Longest alias chain followed before the lookup gives up and returns the id unchanged.</summary>
    public int AliasMaxHops { get; set; } = 5;

    /// <summary>How long a found alias (retired id to survivor) is cached.</summary>
    public TimeSpan AliasHitTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long "this id is not an alias" is cached. Shorter than hits because an alias may be added at any time.</summary>
    public TimeSpan AliasMissTtl { get; set; } = TimeSpan.FromMinutes(2);
}

internal sealed class MappingOptionsValidator : IValidateOptions<MappingOptions>
{
    public ValidateOptionsResult Validate(string? name, MappingOptions options)
    {
        var failures = new List<string>();

        if (options.MaxRecordsPerCanonicalId < 1)
        {
            failures.Add("MaxRecordsPerCanonicalId must be at least 1.");
        }

        if (options.AliasMaxHops is < 1 or > 20)
        {
            failures.Add("AliasMaxHops must be between 1 and 20.");
        }

        if (options.AliasHitTtl <= TimeSpan.Zero || options.AliasMissTtl <= TimeSpan.Zero)
        {
            failures.Add("Alias cache TTLs must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
