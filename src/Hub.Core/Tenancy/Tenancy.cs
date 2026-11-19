using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Core;

/// <summary>Who runs a search: the tenant it is billed to and the calling client within that tenant.</summary>
public sealed record SearchOwner(string Tenant, string Subject);

public interface ITenantSupplierSelector
{
    /// <summary>
    /// Suppliers to query, in the tenant's priority order. A supplier must be enabled for the tenant, registered,
    /// able to search, and (when given) named in <paramref name="requested"/>. Anything else is skipped silently.
    /// </summary>
    IReadOnlyList<SupplierId> Select(string tenant, IReadOnlyList<SupplierId>? requested);

    bool IsEnabled(string tenant, SupplierId supplier);
}

public interface IPricingService
{
    /// <summary>
    /// What the tenant sells a supplier price for, in <paramref name="targetCurrency"/>: converted at the configured rate,
    /// then marked up by the tenant (or by the tenant's own markup for that supplier). Null when no rate converts the currencies.
    /// </summary>
    Money? ToSellingPrice(string tenant, SupplierId supplier, Money supplierPrice, string targetCurrency);
}

internal sealed class TenantSupplierSelector : ITenantSupplierSelector
{
    private readonly ISupplierRegistry _registry;
    private readonly Dictionary<string, SupplierId[]> _enabled;

    public TenantSupplierSelector(ISupplierRegistry registry, IOptions<TenantOptions> options)
    {
        _registry = registry;
        _enabled = options.Value.Tenants.ToDictionary(
            static t => t.Key,
            static t => t.Value.EnabledSuppliers.Select(SupplierId.Parse).ToArray(),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<SupplierId> Select(string tenant, IReadOnlyList<SupplierId>? requested)
    {
        if (!_enabled.TryGetValue(tenant, out var enabled))
        {
            return [];
        }

        return enabled
            .Where(id => requested is null || requested.Contains(id))
            .Where(id => _registry.TryGet(id, out var entry) && entry.Descriptor.Capabilities.HasFlag(SupplierCapabilities.Search))
            .ToArray();
    }

    public bool IsEnabled(string tenant, SupplierId supplier) =>
        _enabled.TryGetValue(tenant, out var enabled) && enabled.Contains(supplier);
}

internal sealed class TenantMarkupPricing : IPricingService
{
    private readonly TenantOptions _tenants;
    private readonly Dictionary<(string From, string To), decimal> _rates = [];

    public TenantMarkupPricing(IOptions<TenantOptions> tenants, IOptions<PricingOptions>? pricing = null)
    {
        _tenants = tenants.Value;
        foreach (var (pair, rate) in pricing?.Value.FxRates ?? [])
        {
            var parts = pair.Split('/');
            _rates[(parts[0], parts[1])] = rate;
            _rates.TryAdd((parts[1], parts[0]), 1m / rate);
        }
    }

    public Money? ToSellingPrice(string tenant, SupplierId supplier, Money supplierPrice, string targetCurrency)
    {
        decimal rate = 1m;
        if (supplierPrice.Currency != targetCurrency && !_rates.TryGetValue((supplierPrice.Currency, targetCurrency), out rate))
        {
            return null;
        }

        var markup = 0m;
        if (_tenants.Tenants.TryGetValue(tenant, out var config))
        {
            markup = config.SupplierMarkupPercent.TryGetValue(supplier.Value, out var override_) ? override_ : config.MarkupPercent;
        }

        return new Money(Math.Round(supplierPrice.Amount * rate * (1m + (markup / 100m)), 2, MidpointRounding.AwayFromZero), targetCurrency);
    }
}
