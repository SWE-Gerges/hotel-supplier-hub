using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hub.Booking;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers quoting and booking. Pair it with <see cref="AddBookingDatabase"/>; the background sweeps are added
    /// unless <paramref name="runBackgroundServices"/> is false (tests drive the sweeps by hand).
    /// </summary>
    public static IServiceCollection AddHubBookings(this IServiceCollection services, IConfiguration configuration, bool runBackgroundServices = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<QuoteOptions>().Bind(configuration.GetSection(QuoteOptions.SectionName)).ValidateOnStart();
        services.AddOptions<BookingOptions>().Bind(configuration.GetSection(BookingOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<QuoteOptions>, QuoteOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BookingOptions>, BookingOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IQuoteStore, EfQuoteStore>();
        services.TryAddSingleton<IBookingStore, EfBookingStore>();
        services.TryAddSingleton<QuoteService>();
        services.TryAddSingleton<BookingProcessor>();
        if (runBackgroundServices)
        {
            services.AddSingleton<BookingSyncService>();
            services.AddSingleton<PayLaterExpiryService>();
            services.AddHostedService(static sp => sp.GetRequiredService<BookingSyncService>());
            services.AddHostedService(static sp => sp.GetRequiredService<PayLaterExpiryService>());
        }

        return services;
    }

    /// <summary>Registers the quote and booking database and creates or migrates its schema at startup.</summary>
    /// <param name="useMigrations">Apply the committed migrations (PostgreSQL) instead of creating the schema from the model (SQLite).</param>
    public static IServiceCollection AddBookingDatabase(this IServiceCollection services, Action<DbContextOptionsBuilder> configure, bool useMigrations)
    {
        services.AddPooledDbContextFactory<BookingDbContext>(configure);
        services.AddSingleton<IHostedService>(sp => new BookingSchemaInitializer(sp.GetRequiredService<IDbContextFactory<BookingDbContext>>(), useMigrations));
        return services;
    }
}

internal sealed class BookingSchemaInitializer(IDbContextFactory<BookingDbContext> contextFactory, bool useMigrations) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (useMigrations)
        {
            await db.ApplyBookingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
