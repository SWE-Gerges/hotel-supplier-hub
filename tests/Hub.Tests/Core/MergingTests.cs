using System.Text.Json;
using Hub.Abstractions;
using Hub.Core;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class HotelMergerTests
{
    private static HotelMerger NewMerger(MergeOptions? options = null, params SupplierId[] priority) =>
        new("search-1", options ?? new MergeOptions(), priority.Length == 0 ? [TestData.A, TestData.B, TestData.C] : priority);

    private static async Task<List<SearchUpdate>> MergeAsync(HotelMerger merger, SupplierId supplier, params Hotel[] hotels)
    {
        var updates = new List<SearchUpdate>();
        await merger.MergeAsync(supplier, hotels, (update, _) =>
        {
            updates.Add(update);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        return updates;
    }

    [Fact]
    public async Task Hotels_with_the_same_canonical_id_become_one_hotel_with_offers_sorted_cheapest_first()
    {
        var merger = NewMerger();
        await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", "Maple Central Hotel", "H-000001", ("Room X", 120m), ("Room Y", 100m)));
        await MergeAsync(merger, TestData.B, TestData.Hotel(TestData.B, "b1", "MAPLE CENTRAL", "H-000001", ("Room Z", 90m)));

        var hotel = merger.Snapshot().ShouldHaveSingleItem();

        hotel.CanonicalId.ShouldBe("H-000001");
        hotel.Offers.Select(static o => o.SellingPrice.Amount).ShouldBe([90m, 100m, 120m]);
        hotel.Sources.ShouldBe([new SupplierHotelRef(TestData.A, "a1"), new SupplierHotelRef(TestData.B, "b1")]);
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "a")]
    public async Task Static_data_comes_from_the_highest_priority_supplier_whatever_the_arrival_order(string first, string second)
    {
        var merger = NewMerger(priority: [TestData.A, TestData.B]);
        var hotels = new Dictionary<string, (SupplierId Supplier, Hotel Hotel)>
        {
            ["a"] = (TestData.A, TestData.Hotel(TestData.A, "a1", "Name From A", "H-000001")),
            ["b"] = (TestData.B, TestData.Hotel(TestData.B, "b1", "Name From B", "H-000001")),
        };

        await MergeAsync(merger, hotels[first].Supplier, hotels[first].Hotel);
        await MergeAsync(merger, hotels[second].Supplier, hotels[second].Hotel);

        merger.Snapshot().Single().Name.ShouldBe("Name From A");
    }

    [Fact]
    public async Task Duplicate_hotels_from_one_supplier_without_a_canonical_id_collapse_by_supplier_and_code()
    {
        var merger = NewMerger();

        await MergeAsync(
            merger,
            TestData.A,
            TestData.Hotel(TestData.A, "a1", offers: ("Room X", 100m)),
            TestData.Hotel(TestData.A, "a1", offers: ("Room Y", 110m)),
            TestData.Hotel(TestData.A, "a2", offers: ("Room X", 105m)));
        await MergeAsync(merger, TestData.B, TestData.Hotel(TestData.B, "a1", offers: ("Room X", 95m)));

        var hotels = merger.Snapshot();

        hotels.Count.ShouldBe(3, "a1 from supplier A, a2 from supplier A, and a1 from supplier B are three different hotels");
        hotels.Single(h => h.Sources[0] == new SupplierHotelRef(TestData.A, "a1")).Offers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Delivering_the_same_batch_again_changes_nothing_and_reports_nothing()
    {
        var merger = NewMerger();
        var hotel = TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: ("Room X", 100m));

        var first = await MergeAsync(merger, TestData.A, hotel);
        var again = await MergeAsync(merger, TestData.A, hotel);

        first.ShouldHaveSingleItem().ShouldBeOfType<HotelsAdded>();
        again.ShouldBeEmpty("a retry that re-delivers identical offers is not news");
        merger.Snapshot().Single().Offers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_cheaper_first_offer_reports_the_price_change()
    {
        var merger = NewMerger();
        await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: ("Room X", 100m)));

        var updates = await MergeAsync(merger, TestData.B, TestData.Hotel(TestData.B, "b1", canonicalId: "H-000001", offers: ("Room Y", 80m)));

        var update = updates.ShouldHaveSingleItem().ShouldBeOfType<HotelUpdated>();
        update.PreviousCheapest.ShouldBe(new Money(100m, "EUR"));
        update.NewCheapest.ShouldBe(new Money(80m, "EUR"));
        update.Hotel.Offers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task New_offers_are_always_reported_even_when_the_cheapest_price_stays_the_same()
    {
        var merger = NewMerger(new MergeOptions(PriceChangeThreshold: 1000m));
        await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: ("Room X", 100m)));

        var updates = await MergeAsync(merger, TestData.B, TestData.Hotel(TestData.B, "b1", canonicalId: "H-000001", offers: ("Room Y", 150m)));

        var update = updates.ShouldHaveSingleItem().ShouldBeOfType<HotelUpdated>();
        update.PreviousCheapest.ShouldBe(update.NewCheapest);
    }

    [Fact]
    public async Task A_small_refresh_of_the_cheapest_price_is_applied_silently_below_the_threshold()
    {
        var merger = NewMerger(new MergeOptions(PriceChangeThreshold: 1m));
        await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: ("Room X", 100m)));

        var small = await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: ("Room X", 99.5m)));
        var large = await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: ("Room X", 90m)));

        small.ShouldBeEmpty();
        large.ShouldHaveSingleItem().ShouldBeOfType<HotelUpdated>();
        merger.Snapshot().Single().Offers[0].SellingPrice.Amount.ShouldBe(90m);
    }

    [Fact]
    public async Task Only_the_cheapest_offers_up_to_the_per_run_limit_are_kept()
    {
        var merger = NewMerger(new MergeOptions(MaxOffersPerHotel: 2));

        await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: [("R1", 130m), ("R2", 110m), ("R3", 120m)]));

        merger.Snapshot().Single().Offers.Select(static o => o.SellingPrice.Amount).ShouldBe([110m, 120m]);
    }

    [Fact]
    public async Task With_mapping_off_hotels_are_not_matched_across_suppliers()
    {
        var merger = NewMerger(new MergeOptions(UseMapping: false));

        await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001"));
        await MergeAsync(merger, TestData.B, TestData.Hotel(TestData.B, "b1", canonicalId: "H-000001"));

        var hotels = merger.Snapshot();
        hotels.Count.ShouldBe(2);
        hotels.ShouldAllBe(static h => h.CanonicalId == null);
    }

    [Fact]
    public async Task Near_identical_rooms_from_different_suppliers_share_a_group_key()
    {
        var merger = NewMerger();
        await MergeAsync(merger, TestData.A, TestData.Hotel(TestData.A, "a1", "Maple Central Hotel", "H-000001", ("Superior Double Room - Sea View", 120m)));
        await MergeAsync(merger, TestData.B, TestData.Hotel(TestData.B, "b1", "HOTEL MAPLE CENTRAL", "H-000001", ("SUPERIOR DBL SEAVIEW", 110m)));

        var offers = merger.Snapshot().Single().Offers;

        offers.Count.ShouldBe(2);
        offers[0].SellingPrice.Amount.ShouldBe(110m, "the cheapest of the group comes first");
        offers.Select(static o => o.GroupKey).Distinct().Count().ShouldBe(1);
        offers[0].GroupKey.ShouldBe("double-seaview-superior|breakfast");
    }

    [Fact]
    public async Task The_snapshot_lists_the_cheapest_hotels_first()
    {
        var merger = NewMerger();
        await MergeAsync(
            merger,
            TestData.A,
            TestData.Hotel(TestData.A, "a1", "Pricey", offers: ("R", 300m)),
            TestData.Hotel(TestData.A, "a2", "Cheap", offers: ("R", 50m)),
            TestData.Hotel(TestData.A, "a3", "Middle", offers: ("R", 120m)));

        merger.Snapshot().Select(static h => h.Name).ShouldBe(["Cheap", "Middle", "Pricey"]);
    }

    [Fact]
    public async Task A_hotel_that_does_not_name_the_supplier_as_a_source_is_refused()
    {
        var merger = NewMerger();

        await Should.ThrowAsync<ArgumentException>(() => MergeAsync(merger, TestData.A, TestData.Hotel(TestData.B, "b1")));
    }

    [Fact]
    public async Task An_empty_batch_is_a_no_op()
    {
        var merger = NewMerger();

        (await MergeAsync(merger, TestData.A)).ShouldBeEmpty();
        merger.HotelCount.ShouldBe(0);
    }

    [Fact]
    public async Task Updates_for_one_hotel_are_emitted_in_the_order_they_happened()
    {
        var merger = NewMerger();
        var seen = new List<decimal>();
        var gate = new SemaphoreSlim(0);

        async Task MergeWith(SupplierId supplier, decimal price, bool wait)
        {
            await merger.MergeAsync(
                supplier,
                [TestData.Hotel(supplier, $"{supplier}-1", canonicalId: "H-000001", offers: ("R", price))],
                async (update, _) =>
                {
                    if (update is HotelUpdated u)
                    {
                        seen.Add(u.NewCheapest!.Value.Amount);
                    }

                    if (wait)
                    {
                        await gate.WaitAsync();
                    }
                },
                CancellationToken.None);
        }

        await MergeWith(TestData.A, 100m, wait: false);
        var slow = MergeWith(TestData.B, 90m, wait: true);
        await Task.Delay(50);
        var contender = MergeWith(TestData.C, 80m, wait: false);
        await Task.Delay(50);

        contender.IsCompleted.ShouldBeFalse("the second writer waits while the first update for the hotel is still being delivered");
        gate.Release();
        await Task.WhenAll(slow, contender);
        seen.ShouldBe([90m, 80m]);
    }

    [Fact]
    public async Task Two_runs_with_different_options_do_not_affect_each_other()
    {
        var tight = NewMerger(new MergeOptions(MaxOffersPerHotel: 1));
        var loose = NewMerger(new MergeOptions(MaxOffersPerHotel: 10, UseMapping: false));
        var hotelA = TestData.Hotel(TestData.A, "a1", canonicalId: "H-000001", offers: [("R1", 100m), ("R2", 110m)]);
        var hotelB = TestData.Hotel(TestData.B, "b1", canonicalId: "H-000001", offers: [("R3", 105m)]);

        await Task.WhenAll(
            Task.Run(async () => { await MergeAsync(tight, TestData.A, hotelA); await MergeAsync(tight, TestData.B, hotelB); }),
            Task.Run(async () => { await MergeAsync(loose, TestData.A, hotelA); await MergeAsync(loose, TestData.B, hotelB); }));

        tight.Snapshot().ShouldHaveSingleItem().Offers.Count.ShouldBe(1);
        var looseHotels = loose.Snapshot();
        looseHotels.Count.ShouldBe(2, "mapping is off for this run only");
        looseHotels.Sum(static h => h.Offers.Count).ShouldBe(3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    [InlineData(55)]
    [InlineData(89)]
    public async Task Concurrent_merging_equals_a_sequential_reference_merge_under_random_interleavings(int seed)
    {
        var batches = GenerateBatches(seed);
        var options = new MergeOptions(MaxOffersPerHotel: 4);

        var reference = NewMerger(options);
        foreach (var (supplier, hotels) in batches)
        {
            await MergeAsync(reference, supplier, hotels);
        }

        var concurrent = NewMerger(options);
        await Task.WhenAll(batches.Select((batch, index) => Task.Run(async () =>
        {
            var jitter = new Random(HashCode.Combine(seed, index));
            for (var spin = jitter.Next(0, 4); spin > 0; spin--)
            {
                await Task.Yield();
            }

            await MergeAsync(concurrent, batch.Supplier, batch.Hotels);
        })));

        Serialize(concurrent.Snapshot()).ShouldBe(Serialize(reference.Snapshot()));
        concurrent.ActiveLockCount.ShouldBe(0, "locks are cleaned up once idle");
    }

    private static string Serialize(IReadOnlyList<Hotel> hotels) => JsonSerializer.Serialize(hotels);

    /// <summary>Batches over a small pool of physical hotels, some mapped and some not, with offers that may be re-delivered.</summary>
    private static List<(SupplierId Supplier, Hotel[] Hotels)> GenerateBatches(int seed)
    {
        var random = new Random(seed);
        var suppliers = new[] { TestData.A, TestData.B, TestData.C };
        var batches = new List<(SupplierId, Hotel[])>();

        for (var b = 0; b < 18; b++)
        {
            var supplier = suppliers[random.Next(suppliers.Length)];
            var hotels = new List<Hotel>();
            for (var h = random.Next(1, 5); h > 0; h--)
            {
                var physical = random.Next(8);
                var mapped = physical < 6;
                var code = $"{supplier}-{physical}";
                var offers = Enumerable.Range(0, random.Next(1, 4))
                    .Select(_ => random.Next(4))
                    .Distinct()
                    .Select(room => ($"Room {room}", Price: 50m + (random.Next(0, 5) * 10m) + (room * 7m) + (supplier == TestData.A ? 0m : 1m)))
                    .ToArray();
                hotels.Add(TestData.Hotel(supplier, code, $"Hotel {physical} via {supplier}", mapped ? $"H-{physical:D6}" : null, offers));
            }

            batches.Add((supplier, hotels.ToArray()));
        }

        // Re-delivered offers must agree with what was sent before, or "last write wins" would make the outcome order dependent.
        var canonicalPrices = new Dictionary<string, decimal>(StringComparer.Ordinal);
        return batches
            .Select(batch => (batch.Item1, batch.Item2.Select(hotel => hotel with
            {
                Offers = hotel.Offers.Select(offer =>
                {
                    var price = canonicalPrices.TryAdd(offer.OfferId, offer.Price.Amount) ? offer.Price.Amount : canonicalPrices[offer.OfferId];
                    return offer with { Price = new Money(price, "EUR"), SellingPrice = new Money(price, "EUR") };
                }).ToArray(),
            }).ToArray()))
            .ToList();
    }
}

public class KeyedAsyncLockTests
{
    [Fact]
    public async Task Callers_that_need_the_same_keys_in_opposite_order_never_deadlock()
    {
        var locks = new KeyedAsyncLock();
        string[] forward = ["a", "b", "c", "d"];
        string[] backward = ["d", "c", "b", "a"];
        var holders = 0;
        var overlaps = 0;

        async Task Worker(string[] keys)
        {
            for (var i = 0; i < 400; i++)
            {
                using (await locks.AcquireAsync(keys, CancellationToken.None))
                {
                    if (Interlocked.Increment(ref holders) > 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    await Task.Yield();
                    Interlocked.Decrement(ref holders);
                }
            }
        }

        var run = Task.WhenAll(Task.Run(() => Worker(forward)), Task.Run(() => Worker(backward)), Task.Run(() => Worker(forward)), Task.Run(() => Worker(backward)));

        await run.WaitAsync(TimeSpan.FromSeconds(20));
        overlaps.ShouldBe(0, "the same key is never held by two callers");
        locks.ActiveKeyCount.ShouldBe(0);
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var locks = new KeyedAsyncLock();

        using var first = await locks.AcquireAsync(["a"], CancellationToken.None);
        using var second = await locks.AcquireAsync(["b"], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        locks.ActiveKeyCount.ShouldBe(2);
    }

    [Fact]
    public async Task Duplicate_keys_in_one_request_are_acquired_once()
    {
        var locks = new KeyedAsyncLock();

        using (await locks.AcquireAsync(["a", "a", "b", "a"], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
        {
            locks.ActiveKeyCount.ShouldBe(2);
        }

        locks.ActiveKeyCount.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_a_waiter_releases_the_keys_it_already_holds_and_leaves_no_residue()
    {
        var locks = new KeyedAsyncLock();
        var blocker = await locks.AcquireAsync(["b"], CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiter = locks.AcquireAsync(["a", "b"], cts.Token).AsTask();
        await Task.Delay(50);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        using (await locks.AcquireAsync(["a"], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
        {
            // "a" was handed back by the cancelled waiter.
        }

        blocker.Dispose();
        locks.ActiveKeyCount.ShouldBe(0);
    }
}

public class RoomGroupKeyTests
{
    [Theory]
    [InlineData("Maple Central Hotel", "Superior Double Room - Sea View", "double-seaview-superior")]
    [InlineData("HOTEL MAPLE CENTRAL", "SUPERIOR DBL SEAVIEW", "double-seaview-superior")]
    [InlineData("The Maple Central", "Double Superior (Sea View)", "double-seaview-superior")]
    [InlineData("Maple Central Hotel", "Superior Double Room, Ocean View", "double-seaview-superior")]
    [InlineData("Maple Central Hotel", "Superior Double with Oceanview", "double-seaview-superior")]
    [InlineData("Maple Central Hotel", "Maple Deluxe Twin", "deluxe-twin")]
    [InlineData("Maple Central Hotel", "DELUXE, TWIN!!", "deluxe-twin")]
    [InlineData("Maple Central Hotel", "Dèluxe Twïn Rôom", "deluxe-twin")]
    [InlineData("Maple Central Hotel", "ÀÉÎÕÜçñß", "aeioucnss")]
    [InlineData("Maple Central Hotel", "ŁódźŻółć Œuvre", "lodzzolc-oeuvre")]
    [InlineData("Maple Central Hotel", "Garden View Twin", "gardenview-twin")]
    [InlineData("Maple Central Hotel", "Twin - Garden View", "gardenview-twin")]
    [InlineData("Maple Central Hotel", "Junior Ste City View", "cityview-junior-suite")]
    [InlineData("Maple Central Hotel", "Dlx Dbl", "deluxe-double")]
    [InlineData("Maple Central Hotel", "Double double DOUBLE", "double")]
    [InlineData("Maple Central Hotel", "Room", "room")]
    [InlineData("Maple Central Hotel", "", "room")]
    [InlineData("Sea Hotel", "Seaview Double", "double-seaview")]
    [InlineData("Hotel", "Standard Room", "standard")]
    public void Normalises_room_names(string hotel, string room, string expected) =>
        RoomGroupKey.NormalizeRoomName(hotel, room).ShouldBe(expected);

    [Fact]
    public void The_key_includes_the_normalised_board()
    {
        RoomGroupKey.Create("Maple Hotel", "Double Room", BoardType.Breakfast).ShouldBe("double|breakfast");
        RoomGroupKey.Create("Maple Hotel", "Double Room", BoardType.RoomOnly).ShouldNotBe(RoomGroupKey.Create("Maple Hotel", "Double Room", BoardType.Breakfast));
    }

    [Fact]
    public void Different_rooms_do_not_collide()
    {
        RoomGroupKey.NormalizeRoomName("Maple Hotel", "Superior Double").ShouldNotBe(RoomGroupKey.NormalizeRoomName("Maple Hotel", "Standard Double"));
        RoomGroupKey.NormalizeRoomName("Maple Hotel", "Double Sea View").ShouldNotBe(RoomGroupKey.NormalizeRoomName("Maple Hotel", "Double City View"));
    }
}
