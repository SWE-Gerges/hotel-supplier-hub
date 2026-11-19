using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class CrosswalkApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private HttpClient Admin => factory.ClientFor(HubApiFactory.AdminKey);

    [Fact]
    public async Task Crosswalk_endpoints_are_for_admins_only()
    {
        var client = factory.ClientFor(HubApiFactory.ClientKey);

        (await client.GetAsync("/api/v1/mapping/crosswalk/review")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/v1/mapping/crosswalk/runs", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await factory.CreateClient().GetAsync("/api/v1/mapping/crosswalk/entries")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_start_up_run_already_mapped_the_demo_cities_and_a_new_run_changes_nothing()
    {
        var entries = await HubApiFactory.ReadJsonAsync(await Admin.GetAsync("/api/v1/mapping/crosswalk/entries?supplier=supplier-b"));
        entries.AsArray().ShouldContain(static e => e!["canonicalCityId"]!.GetValue<string>() == "city-cedarfall" && e["supplierCityCode"]!.GetValue<string>() == "5003");

        var run = await HubApiFactory.ReadJsonAsync(await Admin.PostAsync("/api/v1/mapping/crosswalk/runs", null));

        run["accepted"]!.GetValue<int>().ShouldBe(0);
        run["reviewed"]!.GetValue<int>().ShouldBe(0);
        run["suppliers"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_review_list_shows_why_and_approve_and_reject_decide_it()
    {
        var pending = await HubApiFactory.ReadJsonAsync(await Admin.GetAsync("/api/v1/mapping/crosswalk/review?supplier=supplier-c"));
        var nowhere = pending.AsArray().Single(static r => r!["supplierCityCode"]!.GetValue<string>() == "NOWHEREVILLE")!;
        var zz = pending.AsArray().Single(static r => r!["supplierCityCode"]!.GetValue<string>() == "ALDERMOOR-ZZ")!;
        nowhere["status"]!.GetValue<string>().ShouldBe("pending");
        nowhere["reason"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        nowhere["candidates"]!.AsArray().ShouldNotBeEmpty();

        var approved = await Admin.PostAsJsonAsync($"/api/v1/mapping/crosswalk/review/{nowhere["id"]}/approve", new { canonicalCityId = "city-brightwater-bay" });
        var again = await Admin.PostAsJsonAsync($"/api/v1/mapping/crosswalk/review/{nowhere["id"]}/approve", new { canonicalCityId = "city-brightwater-bay" });
        var rejected = await Admin.PostAsync($"/api/v1/mapping/crosswalk/review/{zz["id"]}/reject", null);

        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HubApiFactory.ReadJsonAsync(approved))["resolvedCityId"]!.GetValue<string>().ShouldBe("city-brightwater-bay");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HubApiFactory.ReadJsonAsync(await Admin.GetAsync("/api/v1/mapping/crosswalk/review?supplier=supplier-c"))).AsArray().ShouldBeEmpty();
        (await HubApiFactory.ReadJsonAsync(await Admin.GetAsync("/api/v1/mapping/crosswalk/review?status=rejected"))).AsArray().ShouldHaveSingleItem();

        var search = await HubApiFactory.ReadJsonAsync(await factory.ClientFor(HubApiFactory.ClientKey).PostAsJsonAsync(
            "/api/v1/searches", HubApiFactory.SearchBody(destination: "city-brightwater-bay", suppliers: ["supplier-c"])));
        search["summary"]!["suppliers"]![0]!["status"]!.GetValue<string>().ShouldBe("succeeded", "supplier-c is now asked for the city it was mapped to");
    }

    [Fact]
    public async Task Bad_input_gets_problem_details()
    {
        (await Admin.GetAsync("/api/v1/mapping/crosswalk/review?status=bogus")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Admin.PostAsync("/api/v1/mapping/crosswalk/review/99999/approve", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Admin.PostAsync("/api/v1/mapping/crosswalk/review/99999/reject", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var unknown = await Admin.PostAsJsonAsync("/api/v1/mapping/crosswalk/review/99999/approve", new { canonicalCityId = "city-nowhere" });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an unknown review is reported before an unknown city");
    }
}
