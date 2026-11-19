using System.Diagnostics.Metrics;
using Hub.Abstractions;
using Hub.Mapping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Hub.Tests;

internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose() => _meters.ForEach(static m => m.Dispose());
}

/// <summary>Counts how often the alias store is asked, so cache behaviour can be observed.</summary>
internal sealed class CountingAliasStore(IAliasStore inner) : IAliasStore
{
    public int Lookups { get; private set; }

    public ValueTask<string?> GetSurvivorAsync(string retiredId, CancellationToken cancellationToken)
    {
        Lookups++;
        return inner.GetSurvivorAsync(retiredId, cancellationToken);
    }

    public ValueTask SetAliasAsync(string retiredId, string survivorId, CancellationToken cancellationToken) =>
        inner.SetAliasAsync(retiredId, survivorId, cancellationToken);

    public ValueTask<IReadOnlyList<string>> GetRetiredIdsAsync(string survivorId, CancellationToken cancellationToken) =>
        inner.GetRetiredIdsAsync(survivorId, cancellationToken);
}

public class AliasResolverTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly TestMeterFactory _meters = new();
    private readonly MappingOptions _options = new();
    private readonly InMemoryHotelIdentityStore _backing;

    public AliasResolverTests() => _backing = new InMemoryHotelIdentityStore(Options.Create(_options));

    public void Dispose() => _meters.Dispose();

    private AliasResolver Resolver(IAliasStore? store = null) =>
        new(store ?? _backing, Options.Create(_options), _time, _meters, NullLogger<AliasResolver>.Instance);

    private async Task Chain(params string[] ids)
    {
        for (var i = 0; i < ids.Length - 1; i++)
        {
            await _backing.SetAliasAsync(ids[i], ids[i + 1], default);
        }
    }

    private long CollectFailures(Func<Task> action)
    {
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == AliasResolver.FailureCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref total, value));
        listener.Start();
        action().GetAwaiter().GetResult();
        return Interlocked.Read(ref total);
    }

    [Fact]
    public async Task An_id_that_is_not_an_alias_resolves_to_itself()
    {
        (await Resolver().ResolveAsync("H-000001", default)).ShouldBe("H-000001");
    }

    [Fact]
    public async Task A_chain_of_aliases_resolves_to_the_final_survivor()
    {
        await Chain("H-1", "H-2", "H-3");

        (await Resolver().ResolveAsync("H-1", default)).ShouldBe("H-3");
        (await Resolver().ResolveAsync("H-2", default)).ShouldBe("H-3");
    }

    [Fact]
    public async Task A_cycle_is_detected_and_leaves_the_id_unchanged()
    {
        await Chain("H-1", "H-2", "H-3", "H-1");

        (await Resolver().ResolveAsync("H-1", default)).ShouldBe("H-1");
        (await Resolver().ResolveAsync("H-2", default)).ShouldBe("H-2");
    }

    [Fact]
    public async Task An_alias_pointing_at_itself_is_a_cycle_too()
    {
        await _backing.SetAliasAsync("H-1", "H-1", default);

        (await Resolver().ResolveAsync("H-1", default)).ShouldBe("H-1");
    }

    [Fact]
    public async Task Chains_up_to_the_hop_limit_are_followed_and_longer_ones_are_left_alone()
    {
        await Chain("H-0", "H-1", "H-2", "H-3", "H-4", "H-5"); // five hops
        (await Resolver().ResolveAsync("H-0", default)).ShouldBe("H-5");

        await _backing.SetAliasAsync("H-5", "H-6", default); // six hops
        (await Resolver().ResolveAsync("H-0", default)).ShouldBe("H-0", "an over-long chain is not followed");
        (await Resolver().ResolveAsync("H-1", default)).ShouldBe("H-6", "a chain of five hops from here is still fine");
    }

    [Fact]
    public async Task Found_aliases_are_cached_for_ten_minutes()
    {
        await Chain("H-1", "H-2");
        var store = new CountingAliasStore(_backing);
        var resolver = Resolver(store);

        await resolver.ResolveAsync("H-1", default);
        var afterFirst = store.Lookups;
        _time.Advance(TimeSpan.FromMinutes(9));
        await resolver.ResolveAsync("H-1", default);
        store.Lookups.ShouldBe(afterFirst, "served from cache");

        _time.Advance(TimeSpan.FromMinutes(2));
        await resolver.ResolveAsync("H-1", default);
        store.Lookups.ShouldBeGreaterThan(afterFirst, "the entry expired after ten minutes");
    }

    [Fact]
    public async Task Misses_are_cached_for_only_two_minutes_so_a_new_alias_shows_up_soon()
    {
        var store = new CountingAliasStore(_backing);
        var resolver = Resolver(store);

        (await resolver.ResolveAsync("H-1", default)).ShouldBe("H-1");
        await _backing.SetAliasAsync("H-1", "H-2", default);
        _time.Advance(TimeSpan.FromMinutes(1));
        (await resolver.ResolveAsync("H-1", default)).ShouldBe("H-1", "the miss is still cached");
        store.Lookups.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(2));
        (await resolver.ResolveAsync("H-1", default)).ShouldBe("H-2");
    }

    [Fact]
    public async Task Invalidating_the_cache_makes_a_new_alias_visible_immediately()
    {
        var resolver = Resolver();
        (await resolver.ResolveAsync("H-1", default)).ShouldBe("H-1");
        await _backing.SetAliasAsync("H-1", "H-2", default);

        resolver.InvalidateAll();

        (await resolver.ResolveAsync("H-1", default)).ShouldBe("H-2");
    }

    [Fact]
    public async Task When_the_store_fails_the_id_is_returned_unchanged_and_the_failure_is_counted()
    {
        var store = Substitute.For<IAliasStore>();
        store.GetSurvivorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("store down"));
        var resolver = Resolver(store);

        string? resolved = null;
        var failures = CollectFailures(async () => resolved = await resolver.ResolveAsync("H-1", default));

        resolved.ShouldBe("H-1", "fail-open: a hotel is never lost because the lookup failed");
        failures.ShouldBe(1);
    }

    [Fact]
    public async Task A_failure_is_not_cached_so_the_next_lookup_tries_the_store_again()
    {
        var store = Substitute.For<IAliasStore>();
        store.GetSurvivorAsync("H-1", Arg.Any<CancellationToken>()).Returns(
            _ => throw new InvalidOperationException("store down"),
            _ => ValueTask.FromResult<string?>("H-2"),
            _ => ValueTask.FromResult<string?>(null));
        var resolver = Resolver(store);

        (await resolver.ResolveAsync("H-1", default)).ShouldBe("H-1");
        (await resolver.ResolveAsync("H-1", default)).ShouldBe("H-2", "the store recovered, and the earlier failure left nothing behind");
    }

    [Fact]
    public async Task Cancellation_is_not_mistaken_for_a_store_failure()
    {
        var store = Substitute.For<IAliasStore>();
        store.GetSurvivorAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(new OperationCanceledException());
        var resolver = Resolver(store);

        long failures = 0;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            failures = CollectFailures(() => resolver.ResolveAsync("H-1", default).AsTask()));

        failures.ShouldBe(0);
    }
}

public class CanonicalResolutionTests
{
    private readonly MappingOptions _options = new();
    private readonly InMemoryHotelIdentityStore _store;
    private readonly AliasResolver _aliases;
    private readonly SupplierCodeMap _codeMap;
    private readonly CanonicalIdResolver _resolver;
    private readonly HotelMappingService _service;

    public CanonicalResolutionTests()
    {
        var options = Options.Create(_options);
        _store = new InMemoryHotelIdentityStore(options);
        _aliases = new AliasResolver(_store, options, new FakeTimeProvider(), new TestMeterFactory(), NullLogger<AliasResolver>.Instance);
        _codeMap = new SupplierCodeMap(_store, _store, _aliases, options);
        _resolver = new CanonicalIdResolver(_store, _aliases, _codeMap);
        _service = new HotelMappingService(_store, _store, _aliases, _codeMap, options);
    }

    private async Task<string> Mint(SupplierId supplier, string code, string? canonical = null) =>
        (await _store.AddAsync(new AddIdentityRequest(supplier, code, canonical), default)).CanonicalId!;

    [Fact]
    public async Task Supplier_codes_resolve_to_canonical_ids_in_batches()
    {
        var hotel = await Mint(TestData.A, "a1");
        await Mint(TestData.B, "b1", hotel);

        var resolved = await _resolver.ResolveHotelsAsync(TestData.B, ["b1", "nope"], default);

        resolved.ShouldBe(new Dictionary<string, string> { ["b1"] = hotel });
        (await _resolver.ResolveHotelAsync(TestData.A, "a1", default)).ShouldBe(hotel);
        (await _resolver.ResolveHotelAsync(TestData.A, "missing", default)).ShouldBeNull();
    }

    [Fact]
    public async Task A_retired_canonical_id_resolves_to_its_survivor()
    {
        var retired = await Mint(TestData.A, "a1");
        var survivor = await Mint(TestData.B, "b1");
        (await _service.SetAliasAsync(retired, survivor, default)).ShouldBe(SetAliasStatus.Set);

        (await _resolver.ResolveHotelAsync(TestData.A, "a1", default)).ShouldBe(survivor);
    }

    [Fact]
    public async Task The_supplier_code_map_holds_one_code_per_supplier_for_a_canonical_id()
    {
        var hotel = await Mint(TestData.A, "a1");
        await Mint(TestData.A, "a1-duplicate", hotel);
        await Mint(TestData.B, "b1", hotel);

        var codes = await _codeMap.GetCodesAsync(hotel, default);

        codes.ShouldBe(new Dictionary<SupplierId, string> { [TestData.A] = "a1", [TestData.B] = "b1" }, ignoreOrder: true);
        (await _resolver.GetSupplierCodeAsync(hotel, TestData.B, default)).ShouldBe("b1");
        (await _resolver.GetSupplierCodeAsync(hotel, TestData.C, default)).ShouldBeNull();
    }

    [Fact]
    public async Task The_code_map_of_a_survivor_includes_the_records_of_hotels_merged_into_it()
    {
        var survivor = await Mint(TestData.A, "a1");
        var retired = await Mint(TestData.C, "c1");
        await _service.SetAliasAsync(retired, survivor, default);

        var viaSurvivor = await _codeMap.GetCodesAsync(survivor, default);
        var viaRetired = await _codeMap.GetCodesAsync(retired, default);

        viaSurvivor.Keys.ShouldBe([TestData.A, TestData.C], ignoreOrder: true);
        viaRetired.ShouldBe(viaSurvivor, ignoreOrder: true);
    }

    [Fact]
    public async Task Setting_an_alias_validates_its_ends()
    {
        var one = await Mint(TestData.A, "a1");
        var two = await Mint(TestData.A, "a2");

        (await _service.SetAliasAsync(one, one, default)).ShouldBe(SetAliasStatus.SelfAlias);
        (await _service.SetAliasAsync(one, "H-999999", default)).ShouldBe(SetAliasStatus.UnknownCanonicalId);
        (await _service.SetAliasAsync("H-999999", one, default)).ShouldBe(SetAliasStatus.UnknownCanonicalId);
        (await _service.SetAliasAsync(one, two, default)).ShouldBe(SetAliasStatus.Set);
        (await _service.SetAliasAsync(two, one, default)).ShouldBe(SetAliasStatus.WouldCreateCycle);
    }

    [Fact]
    public async Task A_longer_cycle_is_also_refused()
    {
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await Mint(TestData.A, $"a{i}"));
        }

        await _service.SetAliasAsync(ids[0], ids[1], default);
        await _service.SetAliasAsync(ids[1], ids[2], default);
        await _service.SetAliasAsync(ids[2], ids[3], default);

        (await _service.SetAliasAsync(ids[3], ids[0], default)).ShouldBe(SetAliasStatus.WouldCreateCycle);
    }

    [Fact]
    public async Task A_new_alias_is_visible_to_lookups_straight_away()
    {
        var a = await Mint(TestData.A, "a1");
        var b = await Mint(TestData.B, "b1");
        (await _resolver.ResolveHotelAsync(TestData.A, "a1", default)).ShouldBe(a);

        await _service.SetAliasAsync(a, b, default);

        (await _resolver.ResolveHotelAsync(TestData.A, "a1", default)).ShouldBe(b);
    }

    [Fact]
    public async Task The_hotel_view_shows_sources_and_follows_merge_history()
    {
        var survivor = await Mint(TestData.A, "a1");
        var retired = await Mint(TestData.B, "b1");
        await _service.SetAliasAsync(retired, survivor, default);

        var view = await _service.GetHotelAsync(retired, default);

        view.ShouldNotBeNull();
        view.RequestedId.ShouldBe(retired);
        view.CanonicalId.ShouldBe(survivor);
        view.RetiredIds.ShouldBe([retired]);
        view.Sources.Select(static s => s.SupplierCode).ShouldBe(["a1", "b1"]);
        (await _service.GetHotelAsync("H-424242", default)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 5, "00:10:00", "00:02:00")]
    [InlineData(10, 0, "00:10:00", "00:02:00")]
    [InlineData(10, 21, "00:10:00", "00:02:00")]
    [InlineData(10, 5, "00:00:00", "00:02:00")]
    [InlineData(10, 5, "00:10:00", "00:00:00")]
    public void Invalid_mapping_settings_are_rejected(int maxRecords, int hops, string hitTtl, string missTtl)
    {
        var options = new MappingOptions
        {
            MaxRecordsPerCanonicalId = maxRecords,
            AliasMaxHops = hops,
            AliasHitTtl = TimeSpan.Parse(hitTtl, System.Globalization.CultureInfo.InvariantCulture),
            AliasMissTtl = TimeSpan.Parse(missTtl, System.Globalization.CultureInfo.InvariantCulture),
        };

        new MappingOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void The_default_mapping_settings_match_the_documented_ones()
    {
        var options = new MappingOptions();

        options.MaxRecordsPerCanonicalId.ShouldBe(50);
        options.AliasMaxHops.ShouldBe(5);
        options.AliasHitTtl.ShouldBe(TimeSpan.FromMinutes(10));
        options.AliasMissTtl.ShouldBe(TimeSpan.FromMinutes(2));
        new MappingOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }
}
