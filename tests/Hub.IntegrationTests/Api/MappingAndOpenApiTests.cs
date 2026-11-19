using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class MappingApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private HttpClient Admin => factory.ClientFor(HubApiFactory.AdminKey);

    private static Task<HttpResponseMessage> AddAsync(HttpClient admin, string supplier, string code, string? canonicalId = null) =>
        admin.PostAsJsonAsync("/api/v1/mapping/identities", new { supplier, supplierCode = code, canonicalId });

    [Fact]
    public async Task Adding_an_identity_mints_a_canonical_id_and_returns_where_to_find_it()
    {
        var response = await AddAsync(Admin, "supplier-a", "api-test-1");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await HubApiFactory.ReadJsonAsync(response);
        var id = body["canonicalId"]!.GetValue<string>();
        id.ShouldStartWith("H-");
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/mapping/hotels/{id}");
        body["status"]!.GetValue<string>().ShouldBe("created");

        var hotel = await HubApiFactory.ReadJsonAsync(await Admin.GetAsync($"/api/v1/mapping/hotels/{id}"));
        hotel["sources"]!.AsArray().Single()!["code"]!.GetValue<string>().ShouldBe("api-test-1");
    }

    [Fact]
    public async Task A_second_supplier_code_can_be_attached_and_repeating_it_is_idempotent()
    {
        var id = (await HubApiFactory.ReadJsonAsync(await AddAsync(Admin, "supplier-a", "api-test-2")))["canonicalId"]!.GetValue<string>();

        var attached = await AddAsync(Admin, "supplier-b", "api-test-2b", id);
        var again = await AddAsync(Admin, "supplier-b", "api-test-2b", id);

        attached.StatusCode.ShouldBe(HttpStatusCode.Created);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HubApiFactory.ReadJsonAsync(again))["status"]!.GetValue<string>().ShouldBe("alreadyExists");
        var hotel = await HubApiFactory.ReadJsonAsync(await Admin.GetAsync($"/api/v1/mapping/hotels/{id}"));
        hotel["sources"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_supplier_code_cannot_belong_to_two_canonical_hotels()
    {
        var first = (await HubApiFactory.ReadJsonAsync(await AddAsync(Admin, "supplier-a", "api-test-3")))["canonicalId"]!.GetValue<string>();
        var second = (await HubApiFactory.ReadJsonAsync(await AddAsync(Admin, "supplier-a", "api-test-3-other")))["canonicalId"]!.GetValue<string>();

        var conflict = await AddAsync(Admin, "supplier-a", "api-test-3", second);

        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        conflict.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await conflict.Content.ReadAsStringAsync()).ShouldContain(first);
    }

    [Fact]
    public async Task Attaching_to_an_unknown_canonical_id_is_a_404()
    {
        var response = await AddAsync(Admin, "supplier-a", "api-test-4", "H-987654");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Not A Supplier", "code", null)]
    [InlineData("supplier-a", "", null)]
    [InlineData("supplier-a", "code", "banana")]
    public async Task Invalid_identity_requests_are_a_400(string supplier, string code, string? canonicalId)
    {
        var response = await AddAsync(Admin, supplier, code, canonicalId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await HubApiFactory.ReadJsonAsync(response))["errors"].ShouldNotBeNull();
    }

    [Fact]
    public async Task An_alias_makes_a_retired_id_resolve_to_its_survivor_and_cycles_are_refused()
    {
        var retired = (await HubApiFactory.ReadJsonAsync(await AddAsync(Admin, "supplier-a", "api-test-5a")))["canonicalId"]!.GetValue<string>();
        var survivor = (await HubApiFactory.ReadJsonAsync(await AddAsync(Admin, "supplier-b", "api-test-5b")))["canonicalId"]!.GetValue<string>();

        var set = await Admin.PutAsJsonAsync("/api/v1/mapping/aliases", new { retiredId = retired, survivorId = survivor });
        var view = await HubApiFactory.ReadJsonAsync(await Admin.GetAsync($"/api/v1/mapping/hotels/{retired}"));
        var cycle = await Admin.PutAsJsonAsync("/api/v1/mapping/aliases", new { retiredId = survivor, survivorId = retired });

        set.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        view["requestedId"]!.GetValue<string>().ShouldBe(retired);
        view["canonicalId"]!.GetValue<string>().ShouldBe(survivor);
        view["sources"]!.AsArray().Count.ShouldBe(2, "the survivor now owns the retired hotel's supplier records too");
        cycle.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var survivorView = await HubApiFactory.ReadJsonAsync(await Admin.GetAsync($"/api/v1/mapping/hotels/{survivor}"));
        survivorView["retiredIds"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe([retired]);
    }

    [Fact]
    public async Task Alias_requests_are_validated()
    {
        var self = await Admin.PutAsJsonAsync("/api/v1/mapping/aliases", new { retiredId = "H-000001", survivorId = "H-000001" });
        var unknown = await Admin.PutAsJsonAsync("/api/v1/mapping/aliases", new { retiredId = "H-000001", survivorId = "H-987654" });
        var malformed = await Admin.PutAsJsonAsync("/api/v1/mapping/aliases", new { retiredId = "x", survivorId = "y" });

        self.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_hotel_is_a_404()
    {
        (await Admin.GetAsync("/api/v1/mapping/hotels/H-987654")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Admin.GetAsync("/api/v1/mapping/hotels/banana")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

public class OpenApiSnapshotTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private const string UpdateVariable = "HUB_UPDATE_OPENAPI";

    [Fact]
    public async Task The_generated_document_matches_the_committed_one()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var generated = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var path = Path.Combine(FindRepositoryRoot(), "docs", "openapi.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                NewLine = "\n",
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            await File.WriteAllTextAsync(path, generated.ToJsonString(options) + "\n");
            return;
        }

        File.Exists(path).ShouldBeTrue($"docs/openapi.json is missing. Regenerate it with: {UpdateVariable}=1 dotnet test tests/Hub.IntegrationTests --filter-class Hub.Tests.OpenApiSnapshotTests");
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path))!;

        JsonNode.DeepEquals(generated, committed).ShouldBeTrue(
            $"The API contract changed. If that is intended, regenerate docs/openapi.json with: {UpdateVariable}=1 dotnet test tests/Hub.IntegrationTests --filter-class Hub.Tests.OpenApiSnapshotTests");
    }

    [Fact]
    public async Task The_document_describes_every_endpoint_and_the_api_key_scheme()
    {
        var document = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json"))!;

        var paths = document["paths"]!.AsObject().Select(static p => p.Key).ToList();
        paths.ShouldBe(
        [
            "/api/v1/searches",
            "/api/v1/searches/stream",
            "/api/v1/searches/{searchId}/events",
            "/api/v1/searches/{searchId}",
            "/api/v1/suppliers",
            "/api/v1/admin/suppliers/{supplierId}/faults",
            "/api/v1/mapping/identities",
            "/api/v1/mapping/aliases",
            "/api/v1/mapping/hotels/{canonicalId}",
            "/api/v1/offers/{offerId}",
            "/api/v1/quotes",
            "/api/v1/bookings",
            "/api/v1/bookings/{id}",
            "/api/v1/bookings/{id}/sync",
            "/api/v1/bookings/{id}/cancel",
            "/api/v1/bookings/{id}/payments",
            "/api/v1/mapping/crosswalk/runs",
            "/api/v1/mapping/crosswalk/entries",
            "/api/v1/mapping/crosswalk/review",
            "/api/v1/mapping/crosswalk/review/{id}/approve",
            "/api/v1/mapping/crosswalk/review/{id}/reject",
        ], ignoreOrder: true);
        document["components"]!["securitySchemes"]!["ApiKey"]!["name"]!.GetValue<string>().ShouldBe("X-Api-Key");
        document["paths"]!["/api/v1/searches/stream"]!["post"]!["responses"]!["200"]!["content"]!.AsObject().ContainsKey("text/event-stream").ShouldBeTrue();
        document["paths"]!["/api/v1/searches"]!["post"]!["security"].ShouldNotBeNull();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HotelSupplierHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root from the test output folder.");
    }
}
