using Hub.Abstractions;
using Hub.Booking;
using Hub.Core;
using Hub.Mapping;
using Hub.Suppliers.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Hub.Tests;

/// <summary>
/// The real search, quote and booking services over the three fake suppliers and a SQLite file, with a fake clock.
/// Nothing here is stubbed: an offer comes from a real search and a booking really reaches the fake supplier's ledger.
/// </summary>
internal sealed class BookingHarness : IAsyncDisposable
{
    public const string Tenant = "tenant-test";

    private BookingHarness(FakeTimeProvider time, ServiceProvider provider)
    {
        Time = time;
        Provider = provider;
    }

    public FakeTimeProvider Time { get; }

    public ServiceProvider Provider { get; }

    public QuoteService Quotes => Provider.GetRequiredService<QuoteService>();

    public BookingProcessor Bookings => Provider.GetRequiredService<BookingProcessor>();

    public IOfferStore Offers => Provider.GetRequiredService<IOfferStore>();

    public IQuoteStore QuoteStore => Provider.GetRequiredService<IQuoteStore>();

    public IBookingStore BookingStore => Provider.GetRequiredService<IBookingStore>();

    public SearchOrchestrator Orchestrator => Provider.GetRequiredService<SearchOrchestrator>();

    public CrosswalkService Crosswalk => Provider.GetRequiredService<CrosswalkService>();

    public FakeSupplierFaults Faults => Provider.GetRequiredService<FakeSupplierFaults>();

    public FakeSupplierLedgers Ledgers => Provider.GetRequiredService<FakeSupplierLedgers>();

    public static async Task<BookingHarness> CreateAsync(params (string Key, string Value)[] overrides)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2027, 1, 10, 9, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(Path.GetTempPath(), $"hub-booking-{Guid.NewGuid():N}.db");
        var settings = new Dictionary<string, string?>
        {
            ["Hub:FakeSuppliers:Seed"] = "7",
            ["Hub:FakeSuppliers:HotelsPerCity"] = "12",
            ["Hub:FakeSuppliers:SupplierA:PageSize"] = "6",
            ["Hub:FakeSuppliers:SupplierB:BatchSize"] = "6",
            ["Hub:FakeSuppliers:SupplierB:BatchDelay"] = "00:00:00",
            ["Hub:FakeSuppliers:SupplierB:PollInterval"] = "00:00:00.005",
            ["Hub:FakeSuppliers:SupplierB:BookingConfirmDelay"] = "00:00:05",
            ["Hub:FakeSuppliers:SupplierC:RoomsBatchSize"] = "6",
            ["Hub:Tenancy:Tenants:tenant-test:EnabledSuppliers:0"] = "supplier-a",
            ["Hub:Tenancy:Tenants:tenant-test:EnabledSuppliers:1"] = "supplier-b",
            ["Hub:Tenancy:Tenants:tenant-test:EnabledSuppliers:2"] = "supplier-c",
            ["Hub:Tenancy:Tenants:tenant-test:MarkupPercent"] = "10",
            ["Hub:Pricing:FxRates:EUR/USD"] = "1.08",
            ["Hub:Quotes:Lifetime"] = "00:30:00",
            ["Hub:Resilience:Defaults:RetryBaseDelay"] = "00:00:00.001",
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.api.json"), optional: false)
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddHubCore(configuration);
        services.AddHotelMapping(configuration);
        services.AddCrosswalk(configuration);
        services.AddInMemoryIdentityStore();
        services.AddFakeSuppliers(configuration);
        services.AddHubBookings(configuration, runBackgroundServices: false);
        services.AddBookingDatabase(options => options.UseSqlite($"Data Source={path};Pooling=False"), useMigrations: false);
        var crosswalkPath = path + ".crosswalk";
        services.AddPooledDbContextFactory<CrosswalkDbContext>(options => options.UseSqlite($"Data Source={crosswalkPath};Pooling=False"));
        var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<BookingDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await using (var db = await provider.GetRequiredService<IDbContextFactory<CrosswalkDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await provider.GetRequiredService<CrosswalkService>().RunAsync(default);
        var harness = new BookingHarness(time, provider);
        return harness.WithPath(path);
    }

    private string _path = string.Empty;

    private BookingHarness WithPath(string path)
    {
        _path = path;
        return this;
    }

    public static HotelSearchRequest Request(string currency = "EUR", params SupplierId[] suppliers) => new()
    {
        Destination = "city-aldermoor",
        CheckIn = new DateOnly(2027, 3, 1),
        CheckOut = new DateOnly(2027, 3, 4),
        Rooms = [new Occupancy(2)],
        Nationality = "XA",
        Currency = currency,
        Suppliers = suppliers.Length == 0 ? null : suppliers,
    };

    /// <summary>Runs a real search against one supplier and returns the first matching offer as the hub stored it.</summary>
    public async Task<StoredOffer> FindOfferAsync(SupplierId supplier, bool refundable = true, int skip = 0)
    {
        var result = await Orchestrator.SearchToCompletionAsync(Request("EUR", supplier), new SearchOwner(Tenant, "client"));
        var offer = result.Hotels
            .SelectMany(static h => h.Offers)
            .Where(o => o.Refundable == refundable)
            .Skip(skip)
            .First();
        return Offers.Get(Tenant, offer.OfferId) ?? throw new InvalidOperationException("The offer was not stored.");
    }

    public async Task<QuoteView> QuoteAsync(StoredOffer offer)
    {
        var outcome = await Quotes.QuoteAsync(Tenant, offer.Offer.OfferId, CancellationToken.None);
        return outcome.Quote ?? throw new InvalidOperationException($"Quoting failed: {outcome.Failure} {outcome.Detail}");
    }

    public CreateBookingCommand Command(QuoteView quote, string key, string guest = "Test Guest", PaymentMode mode = PaymentMode.PayNow) =>
        new(Tenant, key, quote.QuoteId, quote.SellingPrice.Amount, quote.SellingPrice.Currency, guest, mode);

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
        File.Delete(_path + ".crosswalk");
    }
}
