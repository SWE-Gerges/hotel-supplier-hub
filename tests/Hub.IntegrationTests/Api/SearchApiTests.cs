using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SearchApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private HttpClient Client => factory.ClientFor(HubApiFactory.ClientKey);

    [Fact]
    public async Task A_json_search_returns_merged_hotels_with_per_supplier_outcomes()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await HubApiFactory.ReadJsonAsync(response);
        body["searchId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        body["summary"]!["status"]!.GetValue<string>().ShouldBe("completed");
        var outcomes = body["summary"]!["suppliers"]!.AsArray();
        outcomes.Select(static o => o!["supplier"]!.GetValue<string>()).ShouldBe(["supplier-a", "supplier-b", "supplier-c"]);
        outcomes.ShouldAllBe(static o => o!["status"]!.GetValue<string>() == "succeeded");

        var hotels = body["hotels"]!.AsArray();
        hotels.Count.ShouldBeGreaterThan(10);
        hotels.ShouldContain(static h => h!["sources"]!.AsArray().Count > 1, "the same physical hotel from several suppliers is merged");
        hotels.ShouldAllBe(static h => h!["canonicalId"]!.GetValue<string>().StartsWith("H-", StringComparison.Ordinal));

        foreach (var hotel in hotels)
        {
            var prices = hotel!["offers"]!.AsArray().Select(static o => o!["sellingPrice"]!["amount"]!.GetValue<decimal>()).ToList();
            prices.ShouldBe(prices.Order().ToList(), "offers are sorted cheapest first");
        }

        var cheapest = hotels.Select(static h => h!["offers"]![0]!["sellingPrice"]!["amount"]!.GetValue<decimal>()).ToList();
        cheapest.ShouldBe(cheapest.Order().ToList(), "hotels are sorted cheapest first");
    }

    [Fact]
    public async Task The_selling_price_carries_the_tenant_markup_over_the_supplier_price()
    {
        var body = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(suppliers: ["supplier-a"])));

        var offer = body["hotels"]![0]!["offers"]![0]!;
        var price = offer["price"]!["amount"]!.GetValue<decimal>();
        offer["sellingPrice"]!["amount"]!.GetValue<decimal>().ShouldBe(Math.Round(price * 1.10m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task The_request_can_pick_suppliers_and_max_results_limits_the_hotels()
    {
        var body = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", new
        {
            destination = "city-brightwater",
            checkIn = "2027-03-01",
            checkOut = "2027-03-02",
            rooms = new[] { new { adults = 1 } },
            nationality = "XA",
            currency = "USD",
            maxResults = 3,
            suppliers = new[] { "supplier-c" },
        }));

        body["summary"]!["suppliers"]!.AsArray().Count.ShouldBe(1);
        body["hotels"]!.AsArray().Count.ShouldBe(3);
        body["hotels"]![0]!["offers"]![0]!["price"]!["currency"]!.GetValue<string>().ShouldBe("USD");
    }

    [Fact]
    public async Task A_tenant_only_reaches_the_suppliers_it_has_enabled()
    {
        var limited = factory.ClientFor(HubApiFactory.OtherTenantKey);

        var body = await HubApiFactory.ReadJsonAsync(await limited.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody()));

        body["summary"]!["suppliers"]!.AsArray().Select(static o => o!["supplier"]!.GetValue<string>()).ShouldBe(["supplier-a"]);
    }

    [Fact]
    public async Task A_supplier_that_is_not_enabled_for_the_tenant_is_skipped_even_when_requested()
    {
        var limited = factory.ClientFor(HubApiFactory.OtherTenantKey);

        var body = await HubApiFactory.ReadJsonAsync(await limited.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(suppliers: ["supplier-b"])));

        body["summary"]!["suppliers"]!.AsArray().ShouldBeEmpty();
        body["summary"]!["status"]!.GetValue<string>().ShouldBe("completed");
    }

    [Fact]
    public async Task Merge_options_can_be_chosen_per_search()
    {
        var mapped = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(options: new { maxOffersPerHotel = 1 })));
        var unmapped = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(options: new { useMapping = false })));

        mapped["hotels"]!.AsArray().ShouldAllBe(static h => h!["offers"]!.AsArray().Count == 1);
        unmapped["hotels"]!.AsArray().Count.ShouldBeGreaterThan(mapped["hotels"]!.AsArray().Count, "without mapping nothing is merged across suppliers");
        unmapped["hotels"]!.AsArray().ShouldAllBe(static h => h!["canonicalId"] == null);
    }

    [Fact]
    public async Task Listing_suppliers_shows_capabilities_tenant_enablement_and_circuit_state()
    {
        var all = await HubApiFactory.ReadJsonAsync(await Client.GetAsync("/api/v1/suppliers"));
        var limited = await HubApiFactory.ReadJsonAsync(await factory.ClientFor(HubApiFactory.OtherTenantKey).GetAsync("/api/v1/suppliers"));

        all.AsArray().Count.ShouldBe(3);
        all.AsArray().ShouldAllBe(static s => s!["enabledForTenant"]!.GetValue<bool>() && s["circuit"]!.GetValue<string>() == "closed");
        all[0]!["id"]!.GetValue<string>().ShouldBe("supplier-a");
        all[0]!["searchStyle"]!.GetValue<string>().ShouldBe("pagedSync");
        all[1]!["searchStyle"]!.GetValue<string>().ShouldBe("asyncPolling");
        all[2]!["searchStyle"]!.GetValue<string>().ShouldBe("sessionTwoStep");
        all[0]!["capabilities"]!.AsArray().Select(static c => c!.GetValue<string>()).ShouldBe(["search", "roomAvailability", "preBook", "book", "cancel", "retrieve", "duplicateDetection", "cityDirectory"]);
        all[1]!["capabilities"]!.AsArray().Select(static c => c!.GetValue<string>()).ShouldBe(["search", "roomAvailability", "book", "cancel", "retrieve", "duplicateDetection", "cityDirectory"], "supplier-b has no prebook");
        all[2]!["capabilities"]!.AsArray().Select(static c => c!.GetValue<string>()).ShouldBe(["search", "preBook", "book", "cancel", "duplicateDetection", "cityDirectory"], "supplier-c has no retrieve");
        limited.AsArray().Select(static s => s!["enabledForTenant"]!.GetValue<bool>()).ShouldBe([true, false, false]);
    }

    [Fact]
    public async Task Seeded_identities_can_be_inspected_through_the_mapping_endpoint()
    {
        var admin = factory.ClientFor(HubApiFactory.AdminKey);

        var response = await admin.GetAsync("/api/v1/mapping/hotels/H-000001");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var hotel = await HubApiFactory.ReadJsonAsync(response);
        hotel["canonicalId"]!.GetValue<string>().ShouldBe("H-000001");
        hotel["sources"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task The_correlation_id_is_echoed_or_generated_and_never_trusted_blindly()
    {
        var echoed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/suppliers");
        echoed.Headers.Add("X-Correlation-Id", "trace-me-123");
        var generated = await Client.GetAsync("/api/v1/suppliers");
        var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/v1/suppliers");
        hostile.Headers.Add("X-Correlation-Id", "bad value with spaces");

        (await Client.SendAsync(echoed)).Headers.GetValues("X-Correlation-Id").ShouldBe(["trace-me-123"]);
        generated.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBeNullOrWhiteSpace();
        (await Client.SendAsync(hostile)).Headers.GetValues("X-Correlation-Id").Single().ShouldNotBe("bad value with spaces");
    }

    [Fact]
    public async Task Health_endpoints_need_no_key_and_the_ready_probe_checks_real_dependencies()
    {
        var anonymous = factory.CreateClient();

        var live = await anonymous.GetAsync("/health/live");
        var ready = await anonymous.GetAsync("/health/ready");

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await HubApiFactory.ReadJsonAsync(ready);
        body["status"]!.GetValue<string>().ShouldBe("Healthy");
        body["checks"]!.AsArray().Select(static c => c!["name"]!.GetValue<string>()).ShouldBe(["suppliers", "identity-store", "booking-database", "crosswalk-database"], ignoreOrder: true);
    }

    [Fact]
    public async Task Search_events_and_results_never_contain_secrets()
    {
        var text = await (await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody())).Content.ReadAsStringAsync();

        text.ShouldNotContain(HubApiFactory.ClientKey);
        text.ShouldNotContain(HubApiFactory.AdminKey);
    }
}

public class AuthAndValidationApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private HttpClient Client => factory.ClientFor(HubApiFactory.ClientKey);

    [Fact]
    public async Task A_missing_key_is_a_401_problem()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await HubApiFactory.ReadJsonAsync(response))["status"]!.GetValue<int>().ShouldBe(401);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("")]
    [InlineData("test-client-key-0000-not-a-secre")]
    [InlineData("test-client-key-0000-not-a-secret-and-more")]
    public async Task A_wrong_key_is_a_401(string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        (await client.GetAsync("/api/v1/suppliers")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_endpoints_refuse_a_client_key_with_a_403()
    {
        var faults = await Client.PutAsJsonAsync("/api/v1/admin/suppliers/supplier-b/faults", new { });
        var mapping = await Client.PostAsJsonAsync("/api/v1/mapping/identities", new { supplier = "supplier-a", supplierCode = "x" });
        var alias = await Client.PutAsJsonAsync("/api/v1/mapping/aliases", new { retiredId = "H-000001", survivorId = "H-000002" });
        var hotel = await Client.GetAsync("/api/v1/mapping/hotels/H-000001");

        new[] { faults, mapping, alias, hotel }.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.Forbidden);
        faults.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task An_admin_key_can_also_search()
    {
        var response = await factory.ClientFor(HubApiFactory.AdminKey).PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(suppliers: ["supplier-a"]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static TheoryData<string, object, string> InvalidBodies => new()
    {
        { "currency", HubApiFactory.SearchBody(currency: "euro"), "currency" },
        { "destination", HubApiFactory.SearchBody(destination: ""), "destination" },
        { "suppliers", HubApiFactory.SearchBody(suppliers: ["Not A Supplier"]), "suppliers" },
        { "waitMs", HubApiFactory.SearchBody(waitMs: 5), "waitMs" },
        { "waitMs", HubApiFactory.SearchBody(waitMs: 999_999), "waitMs" },
        { "searchId", HubApiFactory.SearchBody(searchId: "has spaces"), "searchId" },
        { "replaces", HubApiFactory.SearchBody(replaces: "no/slash"), "replaces" },
        { "options", HubApiFactory.SearchBody(options: new { maxOffersPerHotel = 0 }), "options" },
        { "rooms", new { destination = "city-aldermoor", checkIn = "2027-03-01", checkOut = "2027-03-04", nationality = "XA", currency = "EUR" }, "rooms" },
        { "checkOut", new { destination = "city-aldermoor", checkIn = "2027-03-04", checkOut = "2027-03-01", rooms = new[] { new { adults = 1 } }, nationality = "XA", currency = "EUR" }, "checkOut" },
        { "checkIn", new { destination = "city-aldermoor", checkOut = "2027-03-01", rooms = new[] { new { adults = 1 } }, nationality = "XA", currency = "EUR" }, "checkIn" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_search_is_a_400_validation_problem_naming_the_field(string label, object body, string field)
    {
        _ = label;

        var response = await Client.PostAsJsonAsync("/api/v1/searches", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await HubApiFactory.ReadJsonAsync(response);
        problem["errors"]![field].ShouldNotBeNull();
        problem["correlationId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_stream_endpoint_validates_before_it_starts_streaming()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/searches/stream", HubApiFactory.SearchBody(currency: "euro"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem()
    {
        var response = await Client.PostAsync("/api/v1/searches", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_missing_body_is_a_400_problem()
    {
        var response = await Client.PostAsync("/api/v1/searches", new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_routes_are_problem_details_too()
    {
        var response = await Client.GetAsync("/api/v1/nothing-here");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task An_unknown_destination_is_reported_per_supplier_and_not_as_an_http_error()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(destination: "city-atlantis"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await HubApiFactory.ReadJsonAsync(response);
        body["summary"]!["status"]!.GetValue<string>().ShouldBe("failed");
        body["summary"]!["suppliers"]!.AsArray().ShouldAllBe(static o => o!["error"]!["kind"]!.GetValue<string>() == "unsupported");
    }
}

public class UnhandledErrorApiTests : IClassFixture<UnhandledErrorApiTests.ThrowingFactory>
{
    private readonly ThrowingFactory _factory;

    public UnhandledErrorApiTests(ThrowingFactory factory) => _factory = factory;

    public sealed class ThrowingFactory : HubApiFactory
    {
        protected override void ConfigureServicesForTest(IServiceCollection services) =>
            services.AddSingleton<Hub.Core.ITenantSupplierSelector>(new ExplodingSelector());
    }

    private sealed class ExplodingSelector : Hub.Core.ITenantSupplierSelector
    {
        public IReadOnlyList<Hub.Abstractions.SupplierId> Select(string tenant, IReadOnlyList<Hub.Abstractions.SupplierId>? requested) =>
            throw new InvalidOperationException("secret internal detail at C:\\src\\Internal.cs:line 42");

        public bool IsEnabled(string tenant, Hub.Abstractions.SupplierId supplier) => false;
    }

    [Fact]
    public async Task An_unexpected_failure_is_a_generic_500_problem_without_internals()
    {
        var response = await _factory.ClientFor(HubApiFactory.ClientKey).PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody());

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldNotContain("secret internal detail");
        text.ShouldNotContain("Internal.cs");
        text.ShouldNotContain("InvalidOperationException");
        text.ShouldNotContain(" at ");
        var problem = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        problem["status"]!.GetValue<int>().ShouldBe(500);
        problem["correlationId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
    }
}
