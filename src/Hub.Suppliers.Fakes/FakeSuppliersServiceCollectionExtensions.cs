using Hub.Abstractions;
using Hub.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hub.Suppliers.Fakes;

public static class FakeSuppliersServiceCollectionExtensions
{
    /// <summary>Registers the three in-process fake suppliers, their shared synthetic catalogue and fault controls.</summary>
    public static IServiceCollection AddFakeSuppliers(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FakeSuppliersOptions>().Bind(configuration.GetSection(FakeSuppliersOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FakeSuppliersOptions>, FakeSuppliersOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(static sp =>
        {
            var o = sp.GetRequiredService<IOptions<FakeSuppliersOptions>>().Value;
            return new SyntheticCatalog(o.Seed, o.HotelsPerCity, new Dictionary<SupplierId, int>
            {
                [FakeSupplierIds.SupplierA] = o.SupplierA.Coverage,
                [FakeSupplierIds.SupplierB] = o.SupplierB.Coverage,
                [FakeSupplierIds.SupplierC] = o.SupplierC.Coverage,
            });
        });

        services.AddSingleton(static sp =>
        {
            var o = sp.GetRequiredService<IOptions<FakeSuppliersOptions>>().Value;
            return new FakeSupplierFaults(new Dictionary<SupplierId, FaultController>
            {
                [FakeSupplierIds.SupplierA] = new(o.SupplierA.Faults, o.Seed + 1),
                [FakeSupplierIds.SupplierB] = new(o.SupplierB.Faults, o.Seed + 2),
                [FakeSupplierIds.SupplierC] = new(o.SupplierC.Faults, o.Seed + 3),
            });
        });

        services.AddSingleton(static sp =>
        {
            var time = sp.GetRequiredService<TimeProvider>();
            return new FakeSupplierLedgers(new Dictionary<SupplierId, FakeSupplierLedger>
            {
                [FakeSupplierIds.SupplierA] = new("A-", time),
                [FakeSupplierIds.SupplierB] = new("bk_", time),
                [FakeSupplierIds.SupplierC] = new("C/", time),
            });
        });
        services.AddSingleton(static sp => new SupplierAServer(
            sp.GetRequiredService<SyntheticCatalog>(), sp.GetRequiredService<FakeSupplierFaults>().Require(FakeSupplierIds.SupplierA),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<FakeSupplierLedgers>().Require(FakeSupplierIds.SupplierA)));
        services.AddSingleton(static sp => new SupplierBServer(
            sp.GetRequiredService<SyntheticCatalog>(), sp.GetRequiredService<FakeSupplierFaults>().Require(FakeSupplierIds.SupplierB),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<FakeSuppliersOptions>>(),
            sp.GetRequiredService<FakeSupplierLedgers>().Require(FakeSupplierIds.SupplierB)));
        services.AddSingleton(static sp => new SupplierCServer(
            sp.GetRequiredService<SyntheticCatalog>(), sp.GetRequiredService<FakeSupplierFaults>().Require(FakeSupplierIds.SupplierC),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<FakeSuppliersOptions>>(),
            sp.GetRequiredService<FakeSupplierLedgers>().Require(FakeSupplierIds.SupplierC)));
        services.AddSingleton<IPolicyRuleSource, EmbeddedPolicyRuleSource>();
        services.AddSingleton<IRoomExtraMapper, SupplierARoomExtraMapper>();
        services.AddSingleton<IRoomExtraMapper, SupplierBRoomExtraMapper>();
        services.AddSingleton<IRoomExtraMapper, SupplierCRoomExtraMapper>();
        services.TryAddSingleton<RoomExtraMapperRegistry>();

        services.AddSingleton<SupplierANormalizer>();
        services.AddSingleton<SupplierBNormalizer>();
        services.AddSingleton<SupplierCNormalizer>();

        services.AddHotelSupplier<SupplierAClient>(FakeSupplierIds.SupplierA);
        services.AddHotelSupplier<SupplierBClient>(FakeSupplierIds.SupplierB);
        services.AddHotelSupplier<SupplierCClient>(FakeSupplierIds.SupplierC);
        foreach (var id in new[] { FakeSupplierIds.SupplierA, FakeSupplierIds.SupplierB, FakeSupplierIds.SupplierC })
        {
            var supplier = id;
            services.AddSingleton(sp => new CityDirectoryRegistration(supplier, sp.GetRequiredKeyedService<ICityDirectory>(supplier)));
        }
        return services;
    }
}
