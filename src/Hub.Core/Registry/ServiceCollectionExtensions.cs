using Hub.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hub.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a supplier under <paramref name="id"/> in keyed DI. The implementation is exposed through
    /// <see cref="ISupplierDescriptor"/> and through each capability port it implements, so a supplier only
    /// takes part in what it can actually do.
    /// </summary>
    public static IServiceCollection AddHotelSupplier<TImpl>(this IServiceCollection services, SupplierId id)
        where TImpl : class, ISupplierDescriptor
    {
        ArgumentNullException.ThrowIfNull(services);
        if (id.IsDefault)
        {
            throw new ArgumentException("A supplier id is required.", nameof(id));
        }

        if (services.Any(d => d.ServiceType == typeof(SupplierRegistration)
                              && d.ImplementationInstance is SupplierRegistration existing
                              && existing.Id == id))
        {
            throw new InvalidOperationException($"Supplier '{id}' is already registered.");
        }

        services.AddKeyedSingleton<TImpl>(id);
        services.AddKeyedSingleton<ISupplierDescriptor>(id, static (sp, key) => sp.GetRequiredKeyedService<TImpl>(key));
        AddPort<IHotelSearch, TImpl>(services, id);
        AddPort<IRoomAvailability, TImpl>(services, id);
        AddPort<IPreBook, TImpl>(services, id);
        AddPort<IBook, TImpl>(services, id);
        AddPort<ICancel, TImpl>(services, id);
        AddPort<IRetrieve, TImpl>(services, id);
        AddPort<ICityDirectory, TImpl>(services, id);

        services.AddSingleton(new SupplierRegistration(id));
        services.TryAddSingleton<ISupplierRegistry, KeyedSupplierRegistry>();
        return services;
    }

    /// <summary>Registers orchestration, resilience, tenancy and run management.</summary>
    public static IServiceCollection AddHubCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ResilienceOptions>().Bind(configuration.GetSection(ResilienceOptions.SectionName)).ValidateOnStart();
        services.AddOptions<SearchOptions>().Bind(configuration.GetSection(SearchOptions.SectionName)).ValidateOnStart();
        services.AddOptions<TenantOptions>().Bind(configuration.GetSection(TenantOptions.SectionName)).ValidateOnStart();
        services.AddOptions<SamePlaceOptions>().Bind(configuration.GetSection(SamePlaceOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SamePlaceOptions>, SamePlaceOptionsValidator>());
        services.TryAddSingleton<ISamePlaceGuard, SamePlaceGuard>();
        services.AddOptions<PricingOptions>().Bind(configuration.GetSection(PricingOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PricingOptions>, PricingOptionsValidator>());
        services.AddOptions<StreamingOptions>().Bind(configuration.GetSection(StreamingOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<StreamingOptions>, StreamingOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ResilienceOptions>, ResilienceOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TenantOptions>, TenantOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISupplierRegistry, KeyedSupplierRegistry>();
        services.TryAddSingleton<ITenantSupplierSelector, TenantSupplierSelector>();
        services.TryAddSingleton<IPricingService, TenantMarkupPricing>();
        services.TryAddSingleton<ISupplierGateway, SupplierGateway>();
        services.TryAddSingleton<SearchRunRegistry>();
        services.AddOptions<CancellationPolicyOptions>().Bind(configuration.GetSection(CancellationPolicyOptions.SectionName));
        services.AddMetrics();
        services.TryAddSingleton(static sp =>
        {
            var sources = sp.GetServices<IPolicyRuleSource>().ToList();
            if (sp.GetRequiredService<IOptions<CancellationPolicyOptions>>().Value.RulesDirectory is { Length: > 0 } directory)
            {
                sources.Add(new DirectoryPolicyRuleSource(Path.IsPathRooted(directory) ? directory : Path.Combine(AppContext.BaseDirectory, directory)));
            }

            return new PolicyRuleBook(sources);
        });
        services.TryAddSingleton<ICancellationPolicyNormalizer, CancellationPolicyNormalizer>();
        services.AddHostedService<PolicyRulesStartupCheck>();
        services.AddOptions<OfferStoreOptions>().Bind(configuration.GetSection(OfferStoreOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OfferStoreOptions>, OfferStoreOptionsValidator>());
        services.TryAddSingleton<IOfferStore, InMemoryOfferStore>();
        services.TryAddSingleton<SearchOrchestrator>();
        services.TryAddSingleton<SearchStreams>();
        return services;
    }

    private static void AddPort<TPort, TImpl>(IServiceCollection services, SupplierId id)
        where TPort : class
        where TImpl : class
    {
        if (typeof(TPort).IsAssignableFrom(typeof(TImpl)))
        {
            services.AddKeyedSingleton<TPort>(id, static (sp, key) => (TPort)(object)sp.GetRequiredKeyedService<TImpl>(key));
        }
    }
}
