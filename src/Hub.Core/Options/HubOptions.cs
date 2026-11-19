using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Core;

/// <summary>Resilience settings per supplier; every field is optional so overrides only state what differs.</summary>
public sealed class SupplierResilienceOptions
{
    /// <summary>Time budget for one attempt. A hang consumes this and counts as a circuit-breaker failure.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Total attempts including the first; retries apply to retryable failures only.</summary>
    public int? MaxAttempts { get; set; }

    public TimeSpan? RetryBaseDelay { get; set; }

    public bool? RetryJitter { get; set; }

    public double? CircuitFailureRatio { get; set; }

    public int? CircuitMinimumThroughput { get; set; }

    public TimeSpan? CircuitSamplingDuration { get; set; }

    public TimeSpan? CircuitBreakDuration { get; set; }

    /// <summary>Maximum in-flight calls (bulkhead size). Falls back to the supplier descriptor.</summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>Calls allowed to wait for a bulkhead slot before being rejected.</summary>
    public int? QueueLimit { get; set; }
}

/// <summary>The fully resolved settings a supplier's pipeline is built from.</summary>
public sealed record ResolvedResilience(
    TimeSpan Timeout,
    int MaxAttempts,
    TimeSpan RetryBaseDelay,
    bool RetryJitter,
    double CircuitFailureRatio,
    int CircuitMinimumThroughput,
    TimeSpan CircuitSamplingDuration,
    TimeSpan CircuitBreakDuration,
    int MaxConcurrency,
    int QueueLimit);

public sealed class ResilienceOptions
{
    public const string SectionName = "Hub:Resilience";

    public SupplierResilienceOptions Defaults { get; set; } = new()
    {
        MaxAttempts = 3,
        RetryBaseDelay = TimeSpan.FromMilliseconds(200),
        RetryJitter = true,
        CircuitFailureRatio = 0.5,
        CircuitMinimumThroughput = 10,
        CircuitSamplingDuration = TimeSpan.FromSeconds(30),
        CircuitBreakDuration = TimeSpan.FromSeconds(30),
        QueueLimit = 20,
    };

    /// <summary>Overrides keyed by supplier id.</summary>
    public Dictionary<string, SupplierResilienceOptions> Suppliers { get; set; } = new(StringComparer.Ordinal);

    public ResolvedResilience Resolve(ISupplierDescriptor descriptor)
    {
        Suppliers.TryGetValue(descriptor.SupplierId.Value, out var o);
        var d = Defaults;

        return new ResolvedResilience(
            Timeout: o?.Timeout ?? d.Timeout ?? descriptor.DefaultTimeout,
            MaxAttempts: o?.MaxAttempts ?? d.MaxAttempts ?? 3,
            RetryBaseDelay: o?.RetryBaseDelay ?? d.RetryBaseDelay ?? TimeSpan.FromMilliseconds(200),
            RetryJitter: o?.RetryJitter ?? d.RetryJitter ?? true,
            CircuitFailureRatio: o?.CircuitFailureRatio ?? d.CircuitFailureRatio ?? 0.5,
            CircuitMinimumThroughput: o?.CircuitMinimumThroughput ?? d.CircuitMinimumThroughput ?? 10,
            CircuitSamplingDuration: o?.CircuitSamplingDuration ?? d.CircuitSamplingDuration ?? TimeSpan.FromSeconds(30),
            CircuitBreakDuration: o?.CircuitBreakDuration ?? d.CircuitBreakDuration ?? TimeSpan.FromSeconds(30),
            MaxConcurrency: o?.MaxConcurrency ?? d.MaxConcurrency ?? descriptor.MaxConcurrency,
            QueueLimit: o?.QueueLimit ?? d.QueueLimit ?? 20);
    }
}

internal sealed class ResilienceOptionsValidator : IValidateOptions<ResilienceOptions>
{
    public ValidateOptionsResult Validate(string? name, ResilienceOptions options)
    {
        var failures = new List<string>();

        foreach (var key in options.Suppliers.Keys.Where(static k => !SupplierId.TryParse(k, out _)))
        {
            failures.Add($"Resilience override key '{key}' is not a valid supplier id.");
        }

        Check("Defaults", options.Defaults, failures);
        foreach (var (key, value) in options.Suppliers)
        {
            Check($"Suppliers:{key}", value, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(string path, SupplierResilienceOptions o, List<string> failures)
    {
        if (o.Timeout is { } t && t <= TimeSpan.Zero)
        {
            failures.Add($"{path}: Timeout must be positive.");
        }

        if (o.MaxAttempts is < 1 or > 10)
        {
            failures.Add($"{path}: MaxAttempts must be between 1 and 10.");
        }

        if (o.RetryBaseDelay is { } delay && delay < TimeSpan.Zero)
        {
            failures.Add($"{path}: RetryBaseDelay cannot be negative.");
        }

        if (o.CircuitFailureRatio is <= 0 or > 1)
        {
            failures.Add($"{path}: CircuitFailureRatio must be in (0, 1].");
        }

        if (o.CircuitMinimumThroughput is < 2)
        {
            failures.Add($"{path}: CircuitMinimumThroughput must be at least 2.");
        }

        if (o.CircuitSamplingDuration is { } sampling && sampling < TimeSpan.FromMilliseconds(500))
        {
            failures.Add($"{path}: CircuitSamplingDuration must be at least 500ms.");
        }

        if (o.CircuitBreakDuration is { } brk && brk < TimeSpan.FromMilliseconds(500))
        {
            failures.Add($"{path}: CircuitBreakDuration must be at least 500ms.");
        }

        if (o.MaxConcurrency is < 1)
        {
            failures.Add($"{path}: MaxConcurrency must be at least 1.");
        }

        if (o.QueueLimit is < 0)
        {
            failures.Add($"{path}: QueueLimit cannot be negative.");
        }
    }
}

/// <summary>Defaults for searches; individual runs may override the merge settings.</summary>
public sealed class SearchOptions
{
    public const string SectionName = "Hub:Search";

    /// <summary>Wall-clock budget for a whole search; suppliers still running at expiry are reported as timed out.</summary>
    public TimeSpan GlobalDeadline { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Capacity of the bounded update channel; a slow consumer applies back-pressure to suppliers.</summary>
    public int UpdateChannelCapacity { get; set; } = 256;

    /// <summary>How long a search waits for canonical ids before giving up and leaving that supplier's hotels unmapped.</summary>
    public TimeSpan IdentityLookupTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public bool UseMapping { get; set; } = true;

    public int MaxOffersPerHotel { get; set; } = 25;

    public decimal PriceChangeThreshold { get; set; }

    public MergeOptions ToMergeOptions() => new(UseMapping, MaxOffersPerHotel, PriceChangeThreshold);
}

internal sealed class SearchOptionsValidator : IValidateOptions<SearchOptions>
{
    public ValidateOptionsResult Validate(string? name, SearchOptions options)
    {
        var failures = new List<string>();

        if (options.GlobalDeadline <= TimeSpan.Zero)
        {
            failures.Add("GlobalDeadline must be positive.");
        }

        if (options.UpdateChannelCapacity < 1)
        {
            failures.Add("UpdateChannelCapacity must be at least 1.");
        }

        if (options.IdentityLookupTimeout <= TimeSpan.Zero)
        {
            failures.Add("IdentityLookupTimeout must be positive.");
        }

        failures.AddRange(options.ToMergeOptions().Validate());
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class TenantConfig
{
    /// <summary>Enabled suppliers in priority order: the first one wins static hotel data when merging.</summary>
    public List<string> EnabledSuppliers { get; set; } = [];

    /// <summary>Percentage added to the supplier price to form the selling price.</summary>
    public decimal MarkupPercent { get; set; }

    /// <summary>Markup percentages that replace <see cref="MarkupPercent"/> for individual suppliers, by supplier id.</summary>
    public Dictionary<string, decimal> SupplierMarkupPercent { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PricingOptions
{
    public const string SectionName = "Hub:Pricing";

    /// <summary>Exchange rates as "FROM/TO": how many units of TO one unit of FROM buys. The inverse is derived.</summary>
    public Dictionary<string, decimal> FxRates { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class PricingOptionsValidator : IValidateOptions<PricingOptions>
{
    public ValidateOptionsResult Validate(string? name, PricingOptions options)
    {
        var failures = new List<string>();
        foreach (var (pair, rate) in options.FxRates)
        {
            var parts = pair.Split('/');
            if (parts.Length != 2 || parts.Any(static p => p.Length != 3 || !p.All(static c => c is >= 'A' and <= 'Z')) || parts[0] == parts[1])
            {
                failures.Add($"Fx rate '{pair}' must be named like EUR/USD with two different currencies.");
            }

            if (rate <= 0)
            {
                failures.Add($"Fx rate '{pair}' must be positive.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class TenantOptions
{
    public const string SectionName = "Hub:Tenancy";

    public Dictionary<string, TenantConfig> Tenants { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class TenantOptionsValidator : IValidateOptions<TenantOptions>
{
    public ValidateOptionsResult Validate(string? name, TenantOptions options)
    {
        var failures = new List<string>();

        foreach (var (tenant, config) in options.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant))
            {
                failures.Add("Tenant names cannot be empty.");
            }

            foreach (var id in config.EnabledSuppliers.Where(static s => !SupplierId.TryParse(s, out _)))
            {
                failures.Add($"Tenant '{tenant}' enables '{id}', which is not a valid supplier id.");
            }

            if (config.EnabledSuppliers.Distinct(StringComparer.Ordinal).Count() != config.EnabledSuppliers.Count)
            {
                failures.Add($"Tenant '{tenant}' lists a supplier more than once.");
            }

            if (config.MarkupPercent is < 0 or > 100)
            {
                failures.Add($"Tenant '{tenant}': MarkupPercent must be between 0 and 100.");
            }

            foreach (var (supplier, percent) in config.SupplierMarkupPercent)
            {
                if (!SupplierId.TryParse(supplier, out _) || percent is < 0 or > 100)
                {
                    failures.Add($"Tenant '{tenant}': the markup for '{supplier}' needs a valid supplier id and a percentage between 0 and 100.");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
