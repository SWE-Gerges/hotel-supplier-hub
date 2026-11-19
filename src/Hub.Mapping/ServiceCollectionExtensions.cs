using Hub.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the resolvers and operator service. Pair it with exactly one identity store.</summary>
    public static IServiceCollection AddHotelMapping(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MappingOptions>().Bind(configuration.GetSection(MappingOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MappingOptions>, MappingOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<AliasResolver>();
        services.TryAddSingleton<SupplierCodeMap>();
        services.TryAddSingleton<HotelMappingService>();
        services.TryAddSingleton<ICanonicalIdResolver, CanonicalIdResolver>();
        return services;
    }

    public static IServiceCollection AddInMemoryIdentityStore(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryHotelIdentityStore>();
        services.TryAddSingleton<IHotelIdentityStore>(static sp => sp.GetRequiredService<InMemoryHotelIdentityStore>());
        services.TryAddSingleton<IAliasStore>(static sp => sp.GetRequiredService<InMemoryHotelIdentityStore>());
        return services;
    }

    /// <summary>Registers the relational store. The provider is chosen by the caller through <paramref name="configure"/>.</summary>
    /// <param name="schema">How the schema is brought up at startup: created from the model, or migrated.</param>
    /// <param name="prepare">Runs before the schema step, for example to make sure the database itself exists.</param>
    public static IServiceCollection AddEfIdentityStore(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure,
        SchemaInitialization schema = SchemaInitialization.EnsureCreated,
        Func<CancellationToken, Task>? prepare = null)
    {
        services.AddPooledDbContextFactory<MappingDbContext>(configure);
        services.TryAddSingleton<EfHotelIdentityStore>();
        services.TryAddSingleton<IHotelIdentityStore>(static sp => sp.GetRequiredService<EfHotelIdentityStore>());
        services.TryAddSingleton<IAliasStore>(static sp => sp.GetRequiredService<EfHotelIdentityStore>());
        if (schema != SchemaInitialization.None)
        {
            services.AddSingleton<IHostedService>(sp => new MappingSchemaInitializer(sp.GetRequiredService<IDbContextFactory<MappingDbContext>>(), schema, prepare));
        }

        return services;
    }
}

/// <summary>How a relational store gets its schema when the host starts.</summary>
public enum SchemaInitialization
{
    /// <summary>Create the schema from the model if the database is empty. For tests and throwaway databases.</summary>
    EnsureCreated,

    /// <summary>Apply the committed EF Core migrations.</summary>
    Migrate,

    /// <summary>Leave the schema alone.</summary>
    None,
}

internal sealed class MappingSchemaInitializer(
    IDbContextFactory<MappingDbContext> contextFactory, SchemaInitialization schema, Func<CancellationToken, Task>? prepare) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (prepare is not null)
        {
            await prepare(cancellationToken).ConfigureAwait(false);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (schema == SchemaInitialization.Migrate)
        {
            await db.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
