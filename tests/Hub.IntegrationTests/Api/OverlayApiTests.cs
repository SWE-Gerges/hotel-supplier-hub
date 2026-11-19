using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public sealed class OverlayApiFactory(string mode) : HubApiFactory
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Hub:FakeSuppliers:SupplierB:Overlay"] = mode,
    };
}

public class OverlayApiTests
{
    private static async Task<List<JsonNode>> HotelsAsync(HubApiFactory factory)
    {
        var body = await HubApiFactory.ReadJsonAsync(await factory.ClientFor(HubApiFactory.ClientKey).PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody()));
        return body["hotels"]!.AsArray().Select(static h => h!).ToList();
    }

    private static bool HasSource(JsonNode hotel, string supplier) =>
        hotel["sources"]!.AsArray().Any(s => s!["supplier"]!.GetValue<string>() == supplier);

    [Fact]
    public async Task A_correct_localised_overlay_from_supplier_b_names_the_merged_hotels_it_belongs_to()
    {
        await using var factory = new OverlayApiFactory("Correct");

        var hotels = await HotelsAsync(factory);

        var withB = hotels.Where(static h => HasSource(h, "supplier-b") && h["sources"]!.AsArray().Count > 1).ToList();
        withB.ShouldNotBeEmpty();
        withB.ShouldAllBe(static h => h["name"]!.GetValue<string>().StartsWith("Hôtel ", StringComparison.Ordinal), "the overlay names every merged hotel it describes");
        withB.ShouldAllBe(static h => h["address"]!.GetValue<string>().EndsWith("(local)", StringComparison.Ordinal));
        hotels.Where(static h => !HasSource(h, "supplier-b")).ShouldAllBe(static h => !h["name"]!.GetValue<string>().StartsWith("Hôtel ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_overlay_that_belongs_to_a_different_hotel_changes_no_name()
    {
        await using var factory = new OverlayApiFactory("Wrong");

        var hotels = await HotelsAsync(factory);

        hotels.ShouldNotBeEmpty();
        hotels.ShouldAllBe(static h => !h["name"]!.GetValue<string>().StartsWith("Hôtel ", StringComparison.Ordinal), "a bad overlay cannot rename a hotel");
        hotels.ShouldAllBe(static h => !h["address"]!.GetValue<string>().EndsWith("(local)", StringComparison.Ordinal));
        factory.Logs.Entries.ShouldContain(static e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("Localised overlay", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_overlays_the_names_are_the_suppliers_own()
    {
        await using var factory = new OverlayApiFactory("Off");

        var hotels = await HotelsAsync(factory);

        hotels.ShouldAllBe(static h => !h["name"]!.GetValue<string>().StartsWith("Hôtel ", StringComparison.Ordinal));
    }
}
