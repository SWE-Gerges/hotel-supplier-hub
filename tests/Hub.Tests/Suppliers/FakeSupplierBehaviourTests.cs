using System.Text.Json;
using Hub.Abstractions;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SyntheticCatalogTests
{
    private static SyntheticCatalog Create(int seed = 7, int perCity = 24) => new(seed, perCity, new Dictionary<SupplierId, int>
    {
        [FakeSupplierIds.SupplierA] = 90,
        [FakeSupplierIds.SupplierB] = 80,
        [FakeSupplierIds.SupplierC] = 70,
    });

    [Fact]
    public void The_same_seed_gives_the_same_market_and_a_different_seed_a_different_one()
    {
        var one = Create(seed: 1);
        var again = Create(seed: 1);
        var other = Create(seed: 2);

        one.Hotels.Select(static h => h.Name + h.Stars).ShouldBe(again.Hotels.Select(static h => h.Name + h.Stars));
        one.Hotels.Select(static h => h.Stars).ShouldNotBe(other.Hotels.Select(static h => h.Stars));
    }

    [Fact]
    public void The_same_physical_hotel_has_different_codes_and_slightly_different_names_per_supplier()
    {
        var catalog = Create();
        var shared = catalog.Hotels.First(h => new[] { FakeSupplierIds.SupplierA, FakeSupplierIds.SupplierB, FakeSupplierIds.SupplierC }.All(s => catalog.Lists(s, h)));

        var codes = new[] { FakeSupplierIds.SupplierA, FakeSupplierIds.SupplierB, FakeSupplierIds.SupplierC }.Select(s => catalog.CodeFor(s, shared)).ToList();
        var names = new[] { FakeSupplierIds.SupplierA, FakeSupplierIds.SupplierB, FakeSupplierIds.SupplierC }.Select(s => catalog.NameFor(s, shared)).ToList();

        codes.Distinct().Count().ShouldBe(3);
        names.Distinct().Count().ShouldBe(3);
        names.ShouldAllBe(n => n.Contains(shared.Brand, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Suppliers_cover_overlapping_but_different_shares_of_the_market()
    {
        var catalog = Create();
        var a = catalog.Hotels.Count(h => catalog.Lists(FakeSupplierIds.SupplierA, h));
        var c = catalog.Hotels.Count(h => catalog.Lists(FakeSupplierIds.SupplierC, h));

        a.ShouldBeGreaterThan(c);
        catalog.Hotels.Count(h => catalog.Lists(FakeSupplierIds.SupplierA, h) && catalog.Lists(FakeSupplierIds.SupplierC, h)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Codes_resolve_back_to_the_physical_hotel()
    {
        var catalog = Create();
        var hotel = catalog.Hotels.First(h => catalog.Lists(FakeSupplierIds.SupplierB, h));

        catalog.FindByCode(FakeSupplierIds.SupplierB, catalog.CodeFor(FakeSupplierIds.SupplierB, hotel))!.Index.ShouldBe(hotel.Index);
        catalog.FindByCode(FakeSupplierIds.SupplierB, "BX-NOPE").ShouldBeNull();
    }

    [Fact]
    public void Offers_scale_with_nights_and_guests_and_vary_by_supplier()
    {
        var catalog = Create();
        var hotel = catalog.Hotels.First(h => catalog.Lists(FakeSupplierIds.SupplierA, h) && catalog.Lists(FakeSupplierIds.SupplierB, h));
        var stay = new Stay(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 3), [new Occupancy(2)], "EUR");
        var longer = stay with { CheckOut = new DateOnly(2027, 3, 5) };

        var short2 = catalog.OffersFor(FakeSupplierIds.SupplierA, hotel, stay).First();
        var long4 = catalog.OffersFor(FakeSupplierIds.SupplierA, hotel, longer).First(o => o.Room.Code == short2.Room.Code && o.Board == short2.Board);
        var other = catalog.OffersFor(FakeSupplierIds.SupplierB, hotel, stay).FirstOrDefault(o => o.Room.Code == short2.Room.Code && o.Board == short2.Board);

        long4.Total.ShouldBe(short2.Total * 2, tolerance: 0.05m);
        if (other is not null)
        {
            other.Total.ShouldNotBe(short2.Total);
        }
    }
}

public class FaultControllerTests
{
    [Fact]
    public void With_no_faults_every_call_is_clean_and_instant()
    {
        var controller = new FaultController(FaultProfile.None, seed: 1);

        for (var i = 0; i < 50; i++)
        {
            var decision = controller.Next();
            decision.Hang.ShouldBeFalse();
            decision.Error.ShouldBeNull();
            decision.Malformed.ShouldBeFalse();
            decision.Latency.ShouldBe(TimeSpan.Zero);
        }
    }

    [Fact]
    public void Rates_of_one_always_fire_and_latency_stays_in_range()
    {
        var controller = new FaultController(
            new FaultProfile { ErrorRate = 1, ErrorKind = SupplierErrorKind.RateLimited, MalformedRate = 1, HangRate = 1, MinLatencyMs = 10, MaxLatencyMs = 20 },
            seed: 1);

        for (var i = 0; i < 50; i++)
        {
            var decision = controller.Next();
            decision.Hang.ShouldBeTrue();
            decision.Error.ShouldBe(SupplierErrorKind.RateLimited);
            decision.Malformed.ShouldBeTrue();
            decision.Latency.TotalMilliseconds.ShouldBeInRange(10, 20);
        }
    }

    [Fact]
    public void The_tail_latency_applies_to_the_tail_share_of_calls()
    {
        var controller = new FaultController(new FaultProfile { TailRate = 1, TailLatencyMs = 900, MinLatencyMs = 1, MaxLatencyMs = 2 }, seed: 1);

        controller.Next().Latency.ShouldBe(TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public void The_same_seed_rolls_the_same_dice()
    {
        var profile = new FaultProfile { ErrorRate = 0.5, MinLatencyMs = 0, MaxLatencyMs = 100 };
        var first = new FaultController(profile, seed: 99);
        var second = new FaultController(profile, seed: 99);

        for (var i = 0; i < 20; i++)
        {
            first.Next().ShouldBe(second.Next());
        }
    }

    [Fact]
    public void The_profile_can_be_replaced_while_running_and_invalid_profiles_are_refused()
    {
        var controller = new FaultController(FaultProfile.None, seed: 1);

        controller.Set(new FaultProfile { ErrorRate = 1 });
        controller.Next().Error.ShouldNotBeNull();

        Should.Throw<ArgumentException>(() => controller.Set(new FaultProfile { ErrorRate = 2 }));
        Should.Throw<ArgumentException>(() => controller.Set(new FaultProfile { MinLatencyMs = 50, MaxLatencyMs = 10 }));
        controller.Current.ErrorRate.ShouldBe(1, "a refused profile leaves the current one in place");
    }

    [Fact]
    public void Validation_names_every_problem()
    {
        var problems = new FaultProfile { TailRate = -1, HangRate = 5, MinLatencyMs = -1, ErrorKind = (SupplierErrorKind)99 }.Validate().ToList();

        problems.Count.ShouldBe(4);
    }
}

public class SupplierBPollingTests
{
    private static BStartRequest Start => new("5001", new Stay(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 4), [new Occupancy(2)], "EUR"));

    [Fact]
    public async Task Batches_become_available_out_of_order_and_polling_ends_when_all_are_delivered()
    {
        var time = new FakeTimeProvider();
        using var provider = FakeSuppliersHarness.Build(time, ("Hub:FakeSuppliers:SupplierB:BatchDelay", "00:00:00.100"), ("Hub:FakeSuppliers:SupplierB:BatchSize", "2"));
        var server = provider.GetRequiredService<SupplierBServer>();

        var outOfOrderSeen = false;
        for (var attempt = 0; attempt < 5 && !outOfOrderSeen; attempt++)
        {
            var token = JsonSerializer.Deserialize<BStartResponse>(await server.StartSearchAsync(Start, CancellationToken.None), SupplierBJson.Options)!.Token!;

            var arrivals = new List<int>();
            string status;
            do
            {
                var poll = JsonSerializer.Deserialize<BPollResponse>(await server.PollAsync(token, CancellationToken.None), SupplierBJson.Options)!;
                arrivals.AddRange((poll.Batches ?? []).Select(static b => b.BatchId));
                status = poll.Status;
                time.Advance(TimeSpan.FromMilliseconds(100));
            }
            while (status != "done");

            arrivals.Order().ShouldBe(Enumerable.Range(1, arrivals.Count), "every batch is delivered exactly once");
            outOfOrderSeen = !arrivals.SequenceEqual(arrivals.Order());
        }

        outOfOrderSeen.ShouldBeTrue();
    }

    [Fact]
    public async Task Polling_is_capped_and_a_supplier_that_never_finishes_ends_in_a_timeout_failure()
    {
        using var provider = FakeSuppliersHarness.Build(
            overrides:
            [
                ("Hub:FakeSuppliers:SupplierB:BatchDelay", "00:01:00"),
                ("Hub:FakeSuppliers:SupplierB:PollInterval", "00:00:00.010"),
                ("Hub:FakeSuppliers:SupplierB:MaxWait", "00:00:00.300"),
            ]);
        var search = provider.GetRequiredKeyedService<IHotelSearch>(FakeSupplierIds.SupplierB);

        var results = await search.SearchAsync(TestData.Query() with { Codes = new SupplierCityCodes("5001", "900") }, CancellationToken.None).ToListAsync();

        results.Last().IsFailure.ShouldBeTrue();
        results.Last().Error.Kind.ShouldBe(SupplierErrorKind.Timeout);
        results.Count(static r => r.IsFailure).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_token_is_reported_as_an_error_payload()
    {
        using var provider = FakeSuppliersHarness.Build();
        var server = provider.GetRequiredService<SupplierBServer>();

        var poll = JsonSerializer.Deserialize<BPollResponse>(await server.PollAsync("nope", CancellationToken.None), SupplierBJson.Options)!;

        poll.Status.ShouldBe("error");
        poll.Code.ShouldBe(404);
    }
}

public class SupplierCSessionTests
{
    private static SupplierSearchQuery Query => TestData.Query() with { Codes = new SupplierCityCodes("ALDERMOOR", "XAL") };

    [Fact]
    public async Task An_expired_session_is_replaced_once_and_the_search_carries_on()
    {
        var time = new FakeTimeProvider();
        using var provider = FakeSuppliersHarness.Build(time, ("Hub:FakeSuppliers:SupplierC:SessionTtl", "00:00:30"), ("Hub:FakeSuppliers:SupplierC:RoomsBatchSize", "3"));
        var search = provider.GetRequiredKeyedService<IHotelSearch>(FakeSupplierIds.SupplierC);
        await using var batches = search.SearchAsync(Query, CancellationToken.None).GetAsyncEnumerator();

        (await batches.MoveNextAsync()).ShouldBeTrue();
        batches.Current.IsSuccess.ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(31));
        (await batches.MoveNextAsync()).ShouldBeTrue();
        batches.Current.IsSuccess.ShouldBeTrue("the session was reopened transparently");
        batches.Current.Value.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_session_that_keeps_expiring_ends_the_search_with_a_transient_failure()
    {
        var time = new FakeTimeProvider();
        using var provider = FakeSuppliersHarness.Build(time, ("Hub:FakeSuppliers:SupplierC:SessionTtl", "00:00:30"), ("Hub:FakeSuppliers:SupplierC:RoomsBatchSize", "3"));
        var search = provider.GetRequiredKeyedService<IHotelSearch>(FakeSupplierIds.SupplierC);
        await using var batches = search.SearchAsync(Query, CancellationToken.None).GetAsyncEnumerator();

        await batches.MoveNextAsync();
        time.Advance(TimeSpan.FromSeconds(31));
        await batches.MoveNextAsync();
        time.Advance(TimeSpan.FromSeconds(31));

        (await batches.MoveNextAsync()).ShouldBeTrue();
        batches.Current.IsFailure.ShouldBeTrue();
        batches.Current.Error.Kind.ShouldBe(SupplierErrorKind.Transient);
        (await batches.MoveNextAsync()).ShouldBeFalse("the failure ends the stream");
    }

    [Fact]
    public async Task The_server_refuses_rooms_for_an_expired_session()
    {
        var time = new FakeTimeProvider();
        using var provider = FakeSuppliersHarness.Build(time, ("Hub:FakeSuppliers:SupplierC:SessionTtl", "00:00:30"));
        var server = provider.GetRequiredService<SupplierCServer>();
        var stay = new Stay(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 4), [new Occupancy(2)], "EUR");

        var availability = SupplierCXml.Parse(await server.AvailabilityAsync(new CAvailabilityRequest("ALDERMOOR", stay), CancellationToken.None)).Root!;
        var session = (string)availability.Attribute("Session")!;
        var code = (string)availability.Descendants(SupplierCXml.Ns + "Hotel").First().Attribute("Code")!;

        var fresh = SupplierCXml.Parse(await server.GetRoomsAsync(session, code, CancellationToken.None)).Root!;
        fresh.Name.ShouldBe(SupplierCXml.Ns + "RoomsResponse");

        time.Advance(TimeSpan.FromSeconds(31));
        var expired = SupplierCXml.Parse(await server.GetRoomsAsync(session, code, CancellationToken.None)).Root!;
        SupplierCNormalizer.ReadError(expired)!.Raw.ShouldBe("SESSION_EXPIRED");
    }
}
