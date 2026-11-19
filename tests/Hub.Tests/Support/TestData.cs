using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hub.Abstractions;
using Hub.Core;
using Hub.Mapping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Tests;

internal static class TestData
{
    public static readonly SupplierId A = SupplierId.Parse("supplier-a");
    public static readonly SupplierId B = SupplierId.Parse("supplier-b");
    public static readonly SupplierId C = SupplierId.Parse("supplier-c");
    public static readonly SearchOwner Owner = new("tenant-test", "client-1");

    public static HotelSearchRequest Request(
        string currency = "EUR", IReadOnlyList<SupplierId>? suppliers = null, int? maxResults = null) => new()
    {
        Destination = "city-test",
        CheckIn = new DateOnly(2027, 3, 1),
        CheckOut = new DateOnly(2027, 3, 4),
        Rooms = [new Occupancy(2)],
        Nationality = "XA",
        Currency = currency,
        Suppliers = suppliers,
        MaxResults = maxResults,
    };

    public static SupplierSearchQuery Query(string currency = "EUR") => new(Request(currency), new SupplierCityCodes("CITY", "CC"));

    /// <summary>A supplier hotel with one offer per (room, price) pair. Offer ids are stable so re-delivery is idempotent.</summary>
    public static Hotel Hotel(
        SupplierId supplier,
        string code,
        string name = "Maple Central Hotel",
        string? canonicalId = null,
        params (string Room, decimal Price)[] offers) => new()
    {
        CanonicalId = canonicalId,
        Name = name,
        Stars = 4,
        Address = "1 Test Street",
        Location = new GeoPoint(1, 2),
        Offers = (offers.Length == 0 ? [("Standard Double Room", 100m)] : offers)
            .Select(o => Offer(supplier, code, o.Room, o.Price))
            .ToArray(),
        Sources = [new SupplierHotelRef(supplier, code)],
    };

    public static RoomOffer Offer(SupplierId supplier, string code, string room, decimal price, BoardType board = BoardType.Breakfast, string currency = "EUR") => new()
    {
        OfferId = $"{supplier}.{code}.{room}.{board}",
        Supplier = supplier,
        SupplierHotelCode = code,
        RoomName = room,
        Board = board,
        RawBoard = board.ToString(),
        Refundable = true,
        Price = new Money(price, currency),
        SellingPrice = new Money(price, currency),
    };

    public static IReadOnlyList<Hotel> Batch(params Hotel[] hotels) => hotels;

    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            list.Add(item);
        }

        return list;
    }
}

/// <summary>A supplier whose behaviour each test scripts, with counters for calls and concurrency.</summary>
internal sealed class StubSupplier : ISupplierDescriptor, IHotelSearch
{
    private readonly Func<int, SupplierSearchQuery, CancellationToken, IAsyncEnumerable<Result<IReadOnlyList<Hotel>>>> _behaviour;
    private int _calls;
    private int _inFlight;
    private int _maxInFlight;

    public StubSupplier(
        SupplierId id,
        Func<int, SupplierSearchQuery, CancellationToken, IAsyncEnumerable<Result<IReadOnlyList<Hotel>>>> behaviour,
        int maxConcurrency = 8)
    {
        SupplierId = id;
        _behaviour = behaviour;
        MaxConcurrency = maxConcurrency;
    }

    public SupplierId SupplierId { get; }

    public SupplierCapabilities Capabilities => SupplierCapabilities.Search;

    public SearchStyle SearchStyle => SearchStyle.PagedSync;

    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxConcurrency { get; }

    public int Calls => Volatile.Read(ref _calls);

    public int InFlight => Volatile.Read(ref _inFlight);

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Set when a call's cancellation token fired, so tests can prove cancellation reached the supplier.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> SearchAsync(
        SupplierSearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _inFlight);
        InterlockedMax(ref _maxInFlight, now);
        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
        try
        {
            await foreach (var item in _behaviour(call, query, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public static StubSupplier Returning(SupplierId id, params Hotel[] hotels) =>
        new(id, (_, _, _) => One(Result.Ok<IReadOnlyList<Hotel>>(hotels)));

    public static StubSupplier Failing(SupplierId id, SupplierError error) =>
        new(id, (_, _, _) => One(Result.Fail<IReadOnlyList<Hotel>>(error)));

    public static StubSupplier Hanging(SupplierId id) =>
        new(id, (_, _, ct) => HangAsync(ct));

    public static async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> One(Result<IReadOnlyList<Hotel>> result)
    {
        await Task.Yield();
        yield return result;
    }

    public static async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> HangAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}

internal sealed class TestCityCodes : ICityCodeResolver
{
    public ValueTask<SupplierCityCodes?> ResolveAsync(string canonicalCityId, SupplierId supplier, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SupplierCityCodes?>(canonicalCityId == "city-test" ? new SupplierCityCodes("CITY", "CC") : null);
}

internal sealed class TestCanonicalIds(Dictionary<(SupplierId, string), string>? map = null) : ICanonicalIdResolver
{
    private int _lookups;

    public Func<Exception?> FailWith { get; set; } = () => null;

    /// <summary>Awaited before every lookup; lets a test make the identity store slow or unreachable.</summary>
    public Func<CancellationToken, Task> Before { get; set; } = static _ => Task.CompletedTask;

    public int Lookups => Volatile.Read(ref _lookups);

    public async ValueTask<IReadOnlyDictionary<string, string>> ResolveHotelsAsync(
        SupplierId supplier, IReadOnlyCollection<string> supplierCodes, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lookups);
        await Before(cancellationToken);
        if (FailWith() is { } failure)
        {
            throw failure;
        }

        var found = supplierCodes
            .Where(code => map is not null && map.ContainsKey((supplier, code)))
            .ToDictionary(static code => code, code => map![(supplier, code)], StringComparer.Ordinal);
        return found;
    }

    public async ValueTask<string?> ResolveHotelAsync(SupplierId supplier, string supplierCode, CancellationToken cancellationToken) =>
        (await ResolveHotelsAsync(supplier, [supplierCode], cancellationToken)).GetValueOrDefault(supplierCode);

    public ValueTask<string?> GetSupplierCodeAsync(string canonicalId, SupplierId supplier, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);
}

/// <summary>Wires an orchestrator over stub suppliers without a DI container.</summary>
internal sealed class OrchestratorHarness
{
    public OrchestratorHarness(
        IEnumerable<StubSupplier> suppliers,
        Dictionary<string, TenantConfig>? tenants = null,
        ResilienceOptions? resilience = null,
        SearchOptions? search = null,
        TestCanonicalIds? canonicalIds = null,
        TimeProvider? time = null)
    {
        Suppliers = suppliers.ToList();
        Time = time ?? TimeProvider.System;
        Registry = new StaticSupplierRegistry(Suppliers.Select(s => new SupplierEntry(s, s)));

        tenants ??= new Dictionary<string, TenantConfig>
        {
            [Owner.Tenant] = new() { EnabledSuppliers = Suppliers.Select(static s => s.SupplierId.Value).ToList() },
        };
        var tenantOptions = Options.Create(new TenantOptions { Tenants = tenants });

        resilience ??= FastResilience();
        Gateway = new SupplierGateway(Registry, Options.Create(resilience), Time, NullLogger<SupplierGateway>.Instance);
        Runs = new SearchRunRegistry(Time);
        Orchestrator = new SearchOrchestrator(
            Gateway,
            new TenantSupplierSelector(Registry, tenantOptions),
            canonicalIds ?? new TestCanonicalIds(),
            new TestCityCodes(),
            new TenantMarkupPricing(tenantOptions),
            Runs,
            Options.Create(search ?? new SearchOptions { GlobalDeadline = TimeSpan.FromSeconds(10) }),
            Time,
            NullLogger<SearchOrchestrator>.Instance);
    }

    private static SearchOwner Owner => TestData.Owner;

    public List<StubSupplier> Suppliers { get; }

    public TimeProvider Time { get; }

    public StaticSupplierRegistry Registry { get; }

    public SupplierGateway Gateway { get; }

    public SearchRunRegistry Runs { get; }

    public SearchOrchestrator Orchestrator { get; }

    /// <summary>No retries and no backoff, so tests observe exactly what a supplier did.</summary>
    public static ResilienceOptions FastResilience() => new()
    {
        Defaults = new SupplierResilienceOptions
        {
            MaxAttempts = 1,
            RetryBaseDelay = TimeSpan.Zero,
            RetryJitter = false,
            CircuitMinimumThroughput = 100,
            QueueLimit = 20,
        },
    };

    public async Task<List<SearchUpdate>> RunAsync(HotelSearchRequest? request = null, SearchRunOptions? options = null, SearchOwner? owner = null) =>
        await Orchestrator.SearchAsync(request ?? TestData.Request(), owner ?? Owner, options ?? new SearchRunOptions(), CancellationToken.None).ToListAsync();
}

/// <summary>Collects updates as they are produced so a test can wait for a specific one.</summary>
internal sealed class UpdateCollector
{
    private readonly Channel<SearchUpdate> _channel = Channel.CreateUnbounded<SearchUpdate>();

    public List<SearchUpdate> Seen { get; } = [];

    public Task Pump { get; private set; } = Task.CompletedTask;

    public void Start(IAsyncEnumerable<SearchUpdate> updates) =>
        Pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in updates)
                {
                    await _channel.Writer.WriteAsync(update);
                }

                _channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _channel.Writer.TryComplete(ex);
            }
        });

    public async Task<T> WaitForAsync<T>(Func<T, bool>? predicate = null, TimeSpan? timeout = null) where T : SearchUpdate
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        await foreach (var update in _channel.Reader.ReadAllAsync(cts.Token))
        {
            Seen.Add(update);
            if (update is T match && (predicate is null || predicate(match)))
            {
                return match;
            }
        }

        throw new InvalidOperationException($"The stream ended without a matching {typeof(T).Name}.");
    }

    public async Task<List<SearchUpdate>> DrainAsync()
    {
        await foreach (var update in _channel.Reader.ReadAllAsync())
        {
            Seen.Add(update);
        }

        return Seen;
    }
}
