using System.Diagnostics.Metrics;
using Hub.Abstractions;
using Hub.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SamePlaceGuardTests
{
    private static readonly GeoPoint Hotel = new(48.0000, 11.0000);

    private static SamePlaceGuard Guard(ILogger<SamePlaceGuard>? logger = null, IMeterFactory? meters = null, SamePlaceOptions? options = null) =>
        new(
            Options.Create(options ?? new SamePlaceOptions()),
            meters ?? new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            logger ?? NullLogger<SamePlaceGuard>.Instance);

    private static LocalizedOverlay Overlay(string name, GeoPoint? location, string? address = "1 Test Street") => new("xa", name, address, location);

    private static GeoPoint North(double meters) => new(Hotel.Latitude + (meters / 111_195.0), Hotel.Longitude);

    [Theory]
    [InlineData("Maple Central Hotel", "Hôtel Maple Central", 20, true)]
    [InlineData("Maple Central Hotel", "MAPLE CENTRAL", 150, true)]
    [InlineData("Maple Central Hotel", "Maple Central Hotel & Suites", 280, true)]
    [InlineData("Maple Central Hotel", "Maple Central Hotel", 400, false)]
    [InlineData("Maple Central Hotel", "Maple Central Hotel", 5000, false)]
    [InlineData("Maple Central Hotel", "Grand Seaside Palace", 10, false)]
    [InlineData("Maple Central Hotel", "Birch Harbour Resort", 0, false)]
    public void An_overlay_is_the_same_place_only_when_it_is_close_on_the_map_and_alike_in_name(string hotel, string overlayName, int metres, bool expected)
    {
        var verdict = Guard().Evaluate(hotel, Hotel, Overlay(overlayName, North(metres)));

        verdict.SamePlace.ShouldBe(expected, verdict.Reason);
        verdict.DistanceMeters.ShouldNotBeNull();
    }

    [Fact]
    public void The_distance_is_measured_on_the_sphere()
    {
        SamePlaceGuard.DistanceMeters(Hotel, North(1000)).ShouldBe(1000, 5);
        SamePlaceGuard.DistanceMeters(new GeoPoint(0, 0), new GeoPoint(0, 1)).ShouldBe(111_195, 100);
        SamePlaceGuard.DistanceMeters(Hotel, Hotel).ShouldBe(0, 0.001);
    }

    [Theory]
    [InlineData("Maple Central Hotel", "Hotel Maple Central", true)]
    [InlineData("Maple Central Hotel", "Maple Central", true)]
    [InlineData("Maple Central Hotel", "Maple Hotel", false)]
    [InlineData("Maple Central Hotel", "Birch Harbour", false)]
    public void Without_coordinates_only_a_very_close_name_is_accepted(string hotel, string overlayName, bool expected)
    {
        var withoutOverlayLocation = Guard().Evaluate(hotel, Hotel, Overlay(overlayName, null));
        var withoutHotelLocation = Guard().Evaluate(hotel, null, Overlay(overlayName, Hotel));
        var neither = Guard().Evaluate(hotel, null, Overlay(overlayName, null));

        foreach (var verdict in new[] { withoutOverlayLocation, withoutHotelLocation, neither })
        {
            verdict.SamePlace.ShouldBe(expected, verdict.Reason);
            verdict.DistanceMeters.ShouldBeNull();
        }
    }

    [Fact]
    public void The_limits_come_from_options()
    {
        var tight = Guard(options: new SamePlaceOptions { MaxDistanceMeters = 50 });
        var loose = Guard(options: new SamePlaceOptions { MaxDistanceMeters = 1000 });
        var overlay = Overlay("Maple Central Hotel", North(400));

        tight.Evaluate("Maple Central Hotel", Hotel, overlay).SamePlace.ShouldBeFalse();
        loose.Evaluate("Maple Central Hotel", Hotel, overlay).SamePlace.ShouldBeTrue();
    }

    [Fact]
    public void Rejections_are_counted_per_supplier_and_logged_as_warnings()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var logs = new ListLogger<SamePlaceGuard>();
        var guard = Guard(logs, provider.GetRequiredService<IMeterFactory>());
        var counted = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "hub.overlay.rejected")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => counted.Add(tags.ToArray().Single(static t => t.Key == "supplier").Value?.ToString()));
        listener.Start();

        guard.RecordRejection(TestData.B, "Maple Central Hotel", new SamePlaceVerdict(false, "too far", 900, 0.9));

        counted.ShouldBe(["supplier-b"]);
        logs.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Theory]
    [InlineData(0, 0.5, 0.85)]
    [InlineData(300, 1.5, 0.85)]
    [InlineData(300, 0.5, 0.4)]
    public void Invalid_limits_stop_startup(double distance, double nameSimilarity, double nameOnly)
    {
        new SamePlaceOptionsValidator().Validate(null, new SamePlaceOptions { MaxDistanceMeters = distance, MinNameSimilarity = nameSimilarity, NameOnlyMinSimilarity = nameOnly })
            .Failed.ShouldBeTrue();
        new SamePlaceOptionsValidator().Validate(null, new SamePlaceOptions()).Succeeded.ShouldBeTrue();
    }
}

public class OverlayMergingTests
{
    private static readonly GeoPoint Place = new(48.0000, 11.0000);
    private static readonly GeoPoint Elsewhere = new(48.0300, 11.0300);

    private static Hotel Supplier(SupplierId supplier, string name, GeoPoint? location, LocalizedOverlay? overlay = null, string address = "1 Base Street") => new()
    {
        CanonicalId = "H-000001",
        Name = name,
        Stars = 4,
        Address = address,
        Location = location,
        Overlay = overlay,
        Offers = [TestData.Offer(supplier, "code-" + supplier.Value, "Standard Double Room", 100m)],
        Sources = [new SupplierHotelRef(supplier, "code-" + supplier.Value)],
    };

    private static (HotelMerger Merger, ListLogger<SamePlaceGuard> Logs) NewMerger(params SupplierId[] priority)
    {
        var logs = new ListLogger<SamePlaceGuard>();
        var guard = new SamePlaceGuard(Options.Create(new SamePlaceOptions()), new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(), logs);
        return (new HotelMerger("run", new MergeOptions(true, 25, 0m), priority, guard), logs);
    }

    private static async Task<List<SearchUpdate>> MergeAsync(HotelMerger merger, params (SupplierId Supplier, Hotel Hotel)[] batches)
    {
        var updates = new List<SearchUpdate>();
        foreach (var (supplier, hotel) in batches)
        {
            await merger.MergeAsync(supplier, [hotel], (u, _) => { updates.Add(u); return ValueTask.CompletedTask; }, default);
        }

        return updates;
    }

    [Fact]
    public async Task An_overlay_for_the_same_place_gives_the_hotel_its_localised_name_and_address()
    {
        var (merger, _) = NewMerger(TestData.A, TestData.B);
        var good = new LocalizedOverlay("xa", "Hôtel Maple Central", "1 Rue Locale", new GeoPoint(48.0001, 11.0001));

        await MergeAsync(merger, (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place)), (TestData.B, Supplier(TestData.B, "Maple Central", Place, good)));

        var hotel = merger.Snapshot().ShouldHaveSingleItem();
        hotel.Name.ShouldBe("Hôtel Maple Central");
        hotel.Address.ShouldBe("1 Rue Locale");
        hotel.Overlay.ShouldBe(good);
        hotel.Location.ShouldBe(Place, "coordinates stay the base hotel's");
        hotel.Sources.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_overlay_for_a_different_place_cannot_rename_the_hotel()
    {
        var (merger, logs) = NewMerger(TestData.A, TestData.B);
        var bad = new LocalizedOverlay("xa", "Hôtel Birch Harbour", "9 Other Road", Elsewhere);

        var updates = await MergeAsync(
            merger,
            (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place)),
            (TestData.B, Supplier(TestData.B, "Maple Central", Place, bad)),
            (TestData.B, Supplier(TestData.B, "Maple Central", Place, bad, address: "1 Base Street")));

        var hotel = merger.Snapshot().ShouldHaveSingleItem();
        hotel.Name.ShouldBe("Maple Central Hotel");
        hotel.Address.ShouldBe("1 Base Street");
        hotel.Overlay.ShouldBeNull();
        logs.Entries.Count(static e => e.Level == LogLevel.Warning).ShouldBe(1, "the same rejection is logged once per hotel and supplier");
        updates.OfType<HotelUpdated>().ShouldAllBe(static u => u.Hotel.Name == "Maple Central Hotel");
    }

    [Fact]
    public async Task A_suppliers_own_overlay_that_describes_another_place_is_not_applied_either()
    {
        var (merger, _) = NewMerger(TestData.A);
        var bad = new LocalizedOverlay("xa", "Birch Harbour Resort", "9 Other Road", Elsewhere);

        await MergeAsync(merger, (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place, bad)));

        merger.Snapshot().ShouldHaveSingleItem().Name.ShouldBe("Maple Central Hotel");
    }

    [Fact]
    public async Task The_first_supplier_in_priority_order_with_an_acceptable_overlay_wins_and_a_bad_one_is_skipped()
    {
        var (merger, _) = NewMerger(TestData.A, TestData.B, TestData.C);
        var bad = new LocalizedOverlay("xa", "Birch Harbour Resort", "9 Other Road", Elsewhere);
        var good = new LocalizedOverlay("xa", "Maple Central Hotel (local)", "2 Local Lane", Place);

        await MergeAsync(
            merger,
            (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place)),
            (TestData.B, Supplier(TestData.B, "Maple Central", Place, bad)),
            (TestData.C, Supplier(TestData.C, "Maple Central", Place, good)));

        merger.Snapshot().ShouldHaveSingleItem().Overlay.ShouldBe(good);
    }

    [Fact]
    public async Task The_outcome_does_not_depend_on_which_supplier_answers_first()
    {
        var good = new LocalizedOverlay("xa", "Hôtel Maple Central", "1 Rue Locale", new GeoPoint(48.0001, 11.0001));
        var (first, _) = NewMerger(TestData.A, TestData.B);
        var (second, _) = NewMerger(TestData.A, TestData.B);

        await MergeAsync(first, (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place)), (TestData.B, Supplier(TestData.B, "Maple Central", Place, good)));
        await MergeAsync(second, (TestData.B, Supplier(TestData.B, "Maple Central", Place, good)), (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place)));

        var a = first.Snapshot().Single();
        var b = second.Snapshot().Single();
        (b.Name, b.Address, b.Overlay, b.Location).ShouldBe((a.Name, a.Address, a.Overlay, a.Location));
        b.Sources.ShouldBe(a.Sources);
        b.Offers.Select(static o => o.OfferId).ShouldBe(a.Offers.Select(static o => o.OfferId));
    }

    [Fact]
    public async Task An_accepted_overlay_that_arrives_later_is_announced_as_an_update()
    {
        var (merger, _) = NewMerger(TestData.A, TestData.B);
        var good = new LocalizedOverlay("xa", "Hôtel Maple Central", "1 Rue Locale", new GeoPoint(48.0001, 11.0001));

        var updates = await MergeAsync(merger, (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place)), (TestData.B, Supplier(TestData.B, "Maple Central", Place, good)));

        updates.OfType<HotelUpdated>().ShouldHaveSingleItem().Hotel.Name.ShouldBe("Hôtel Maple Central");
    }

    [Fact]
    public async Task Without_a_guard_overlays_are_ignored()
    {
        var merger = new HotelMerger("run", new MergeOptions(true, 25, 0m), [TestData.A, TestData.B]);
        var good = new LocalizedOverlay("xa", "Hôtel Maple Central", "1 Rue Locale", Place);

        await MergeAsync(merger, (TestData.A, Supplier(TestData.A, "Maple Central Hotel", Place)), (TestData.B, Supplier(TestData.B, "Maple Central", Place, good)));

        merger.Snapshot().ShouldHaveSingleItem().Name.ShouldBe("Maple Central Hotel");
    }
}
