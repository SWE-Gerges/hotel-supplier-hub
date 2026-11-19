using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class StreamingApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private static CancellationToken Timeout(int seconds = 60) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private HttpClient Client => factory.ClientFor(HubApiFactory.ClientKey);

    private HttpClient Admin => factory.ClientFor(HubApiFactory.AdminKey);

    [Fact]
    public async Task The_stream_delivers_updates_in_order_with_ids_and_event_names()
    {
        var ct = Timeout();

        using var response = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "stream-order"), ct);
        var messages = await Sse.ReadAllAsync(response, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        response.Headers.GetValues("X-Search-Id").ShouldBe(["stream-order"]);
        response.Headers.Contains("X-Correlation-Id").ShouldBeTrue();

        var events = messages.Where(static m => m.Comment is null).ToList();
        events.Select(static m => int.Parse(m.Id!, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(Enumerable.Range(1, events.Count));
        events.Select(static m => m.Event).Distinct().ShouldBeSubsetOf(["supplierStarted", "hotelsAdded", "hotelUpdated", "supplierFailed", "supplierCompleted", "completed"]);
        events.Last().Event.ShouldBe("completed");
        events.Count(static m => m.Event == "completed").ShouldBe(1);
        events.ShouldAllBe(static m => m.Json["searchId"]!.GetValue<string>() == "stream-order");
        events.Count(static m => m.Event == "supplierStarted").ShouldBe(3);

        foreach (var supplier in new[] { "supplier-a", "supplier-b", "supplier-c" })
        {
            var started = events.FindIndex(m => m.Event == "supplierStarted" && m.Json["supplier"]!.GetValue<string>() == supplier);
            var completed = events.FindIndex(m => m.Event == "supplierCompleted" && m.Json["supplier"]!.GetValue<string>() == supplier);
            started.ShouldBeGreaterThanOrEqualTo(0);
            completed.ShouldBeGreaterThan(started);
        }

        var summary = events.Last().Json["summary"]!;
        summary["status"]!.GetValue<string>().ShouldBe("completed");
        events.Where(static m => m.Event == "hotelsAdded").Sum(static m => m.Json["hotels"]!.AsArray().Count)
            .ShouldBe(summary["hotelCount"]!.GetValue<int>(), "every merged hotel was announced exactly once");
    }

    [Fact]
    public async Task A_cheaper_offer_arriving_later_is_announced_as_an_update_of_a_known_hotel()
    {
        var ct = Timeout();

        using var response = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(), ct);
        var events = (await Sse.ReadAllAsync(response, ct)).Where(static m => m.Comment is null).ToList();

        var added = events.Where(static m => m.Event == "hotelsAdded").SelectMany(static m => m.Json["hotels"]!.AsArray()).Select(static h => h!["canonicalId"]!.GetValue<string>()).ToHashSet();
        var updates = events.Where(static m => m.Event == "hotelUpdated").ToList();
        updates.ShouldNotBeEmpty("the three suppliers list overlapping hotels");
        updates.ShouldAllBe(m => added.Contains(m.Json["hotel"]!["canonicalId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_quiet_stream_sends_heartbeat_comments()
    {
        var ct = Timeout();
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { minLatencyMs = 700, maxLatencyMs = 700 });
        try
        {
            using var response = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(suppliers: ["supplier-b"]), ct);
            var messages = await Sse.ReadAllAsync(response, ct);

            var heartbeats = messages.Count(static m => m.Comment == "heartbeat");
            heartbeats.ShouldBeGreaterThanOrEqualTo(2);
            var firstHeartbeat = messages.FindIndex(static m => m.Comment == "heartbeat");
            var completed = messages.FindIndex(static m => m.Event == "completed");
            firstHeartbeat.ShouldBeLessThan(completed, "heartbeats arrive while the supplier is still working");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task Cancelling_a_running_stream_ends_it_with_a_cancelled_summary_and_frees_the_id()
    {
        var ct = Timeout();
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { hangRate = 1 });
        try
        {
            using var response = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "cancel-me", waitMs: 30_000), ct);
            var reading = Sse.ReadAllAsync(response, ct);
            await Task.Delay(300, ct);

            var cancel = await Client.DeleteAsync("/api/v1/searches/cancel-me", ct);
            var messages = await reading;

            cancel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            var completed = messages.Last(static m => m.Event == "completed").Json["summary"]!;
            completed["status"]!.GetValue<string>().ShouldBe("cancelled");
            completed["suppliers"]!.AsArray().Single(static s => s!["supplier"]!.GetValue<string>() == "supplier-b")!["status"]!.GetValue<string>().ShouldBe("cancelled");
            messages.ShouldNotContain(static m => m.Event == "supplierFailed", "cancelling is not failing");
            (await Client.DeleteAsync("/api/v1/searches/cancel-me", ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task A_json_search_can_be_cancelled_while_it_waits()
    {
        var ct = Timeout();
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { hangRate = 1 });
        try
        {
            var search = Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(searchId: "cancel-json", waitMs: 30_000), ct);

            // A JSON search gives no sign when it has started, so keep trying until the server knows the id.
            HttpStatusCode cancelled;
            do
            {
                cancelled = (await Client.DeleteAsync("/api/v1/searches/cancel-json", ct)).StatusCode;
                if (cancelled == HttpStatusCode.NotFound)
                {
                    await Task.Delay(50, ct);
                }
            }
            while (cancelled == HttpStatusCode.NotFound);

            cancelled.ShouldBe(HttpStatusCode.NoContent);
            var response = await search;

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await HubApiFactory.ReadJsonAsync(response))["summary"]!["status"]!.GetValue<string>().ShouldBe("cancelled");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task Somebody_elses_search_cannot_be_cancelled_or_replaced_and_keeps_running()
    {
        var ct = Timeout();
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { hangRate = 1 });
        try
        {
            using var response = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "mine", waitMs: 30_000), ct);
            var reading = Sse.ReadAllAsync(response, ct);
            await Task.Delay(300, ct);
            var intruder = factory.ClientFor(HubApiFactory.LimitedKey);

            var cancel = await intruder.DeleteAsync("/api/v1/searches/mine", ct);
            var replace = await intruder.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(replaces: "mine", suppliers: ["supplier-a"]), ct);

            cancel.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            cancel.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            replace.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            reading.IsCompleted.ShouldBeFalse("the owner's search is still running");

            (await Client.DeleteAsync("/api/v1/searches/mine", ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            await reading;
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task A_search_id_that_is_still_running_is_a_409()
    {
        var ct = Timeout();
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { hangRate = 1 });
        try
        {
            using var first = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "taken", waitMs: 30_000), ct);
            var reading = Sse.ReadAllAsync(first, ct);
            await Task.Delay(300, ct);

            var second = await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(searchId: "taken"), ct);

            second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            second.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            await Client.DeleteAsync("/api/v1/searches/taken", ct);
            await reading;
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task Starting_a_search_that_replaces_a_running_one_cancels_the_old_one()
    {
        var ct = Timeout();
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { hangRate = 1 });
        try
        {
            using var first = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "old-run", waitMs: 30_000), ct);
            var reading = Sse.ReadAllAsync(first, ct);
            await Task.Delay(300, ct);

            var replacement = await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(replaces: "old-run", suppliers: ["supplier-a"]), ct);

            replacement.StatusCode.ShouldBe(HttpStatusCode.OK);
            var old = await reading;
            old.Last(static m => m.Event == "completed").Json["summary"]!["status"]!.GetValue<string>().ShouldBe("cancelled");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task Cancelling_an_unknown_search_is_a_404_problem()
    {
        var response = await Client.DeleteAsync("/api/v1/searches/never-existed");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_client_that_disconnects_cancels_the_search_it_started()
    {
        var ct = Timeout();
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { hangRate = 1 });
        try
        {
            using var disconnect = new CancellationTokenSource();
            var response = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "walk-away", waitMs: 30_000), ct);
            var first = Sse.ReadAsync(response, disconnect.Token).GetAsyncEnumerator(disconnect.Token);
            await first.MoveNextAsync();

            response.Dispose();
            await disconnect.CancelAsync();

            // The server notices the closed connection and frees the id; until then a delete would still find it.
            var freed = false;
            for (var attempt = 0; attempt < 50 && !freed; attempt++)
            {
                freed = (await Client.DeleteAsync("/api/v1/searches/walk-away", ct)).StatusCode == HttpStatusCode.NotFound;
                if (!freed)
                {
                    await Task.Delay(100, ct);
                }
            }

            freed.ShouldBeTrue();
            factory.Logs.Entries.Where(static e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error)
                .ShouldBeEmpty("walking away from a stream is normal and must not log errors");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }
}

public class FaultInjectionApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private HttpClient Client => factory.ClientFor(HubApiFactory.ClientKey);

    private HttpClient Admin => factory.ClientFor(HubApiFactory.AdminKey);

    private static JsonNode Outcome(JsonNode body, string supplier) =>
        body["summary"]!["suppliers"]!.AsArray().Single(s => s!["supplier"]!.GetValue<string>() == supplier)!;

    [Fact]
    public async Task A_failing_supplier_shows_up_as_a_failed_outcome_while_the_others_complete()
    {
        var applied = await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { errorRate = 1, errorKind = "unavailable" });
        try
        {
            var body = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody()));

            applied.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await HubApiFactory.ReadJsonAsync(applied))["errorKind"]!.GetValue<string>().ShouldBe("unavailable");
            body["summary"]!["status"]!.GetValue<string>().ShouldBe("partial");
            Outcome(body, "supplier-b")["status"]!.GetValue<string>().ShouldBe("failed");
            Outcome(body, "supplier-b")["error"]!["kind"]!.GetValue<string>().ShouldBe("unavailable");
            Outcome(body, "supplier-a")["status"]!.GetValue<string>().ShouldBe("succeeded");
            Outcome(body, "supplier-c")["status"]!.GetValue<string>().ShouldBe("succeeded");
            body["hotels"]!.AsArray().ShouldNotBeEmpty();
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }

        var healed = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody()));
        healed["summary"]!["status"]!.GetValue<string>().ShouldBe("completed");
    }

    [Fact]
    public async Task The_stream_reports_the_failure_as_a_supplier_failed_event()
    {
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { errorRate = 1, errorKind = "auth" });
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var response = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(), cts.Token);
            var events = (await Sse.ReadAllAsync(response, cts.Token)).Where(static m => m.Comment is null).ToList();

            var failed = events.Single(static m => m.Event == "supplierFailed");
            failed.Json["supplier"]!.GetValue<string>().ShouldBe("supplier-b");
            failed.Json["error"]!["kind"]!.GetValue<string>().ShouldBe("auth");
            events.Count(static m => m.Event == "supplierCompleted").ShouldBe(2);
            events.Last().Json["summary"]!["status"]!.GetValue<string>().ShouldBe("partial");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task A_hung_supplier_is_cut_off_at_the_deadline_and_the_rest_come_back()
    {
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-c", new { hangRate = 1 });
        try
        {
            var started = DateTime.UtcNow;
            var body = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(waitMs: 3000)));

            (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
            body["summary"]!["status"]!.GetValue<string>().ShouldBe("partial");
            Outcome(body, "supplier-c")["error"]!["kind"]!.GetValue<string>().ShouldBe("timeout");
            Outcome(body, "supplier-a")["status"]!.GetValue<string>().ShouldBe("succeeded");
            body["hotels"]!.AsArray().ShouldNotBeEmpty("partial results are first-class");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-c");
        }
    }

    [Fact]
    public async Task A_malformed_payload_is_contained_to_its_supplier()
    {
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-a", new { malformedRate = 1 });
        try
        {
            var body = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody()));

            Outcome(body, "supplier-a")["error"]!["kind"]!.GetValue<string>().ShouldBe("unknown");
            Outcome(body, "supplier-b")["status"]!.GetValue<string>().ShouldBe("succeeded");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-a");
        }
    }

    [Theory]
    [InlineData("supplier-zzz", 404)]
    [InlineData("Not A Supplier", 404)]
    public async Task Faults_can_only_be_set_on_a_supplier_that_exists(string supplier, int status)
    {
        var response = await HubApiFactory.SetFaultsAsync(Admin, Uri.EscapeDataString(supplier), new { errorRate = 1 });

        ((int)response.StatusCode).ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("{\"errorRate\":2}")]
    [InlineData("{\"hangRate\":-1}")]
    [InlineData("{\"minLatencyMs\":500,\"maxLatencyMs\":100}")]
    public async Task An_invalid_fault_profile_is_a_400(string json)
    {
        var response = await Admin.PutAsync(
            "/api/v1/admin/suppliers/supplier-b/faults", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }
}

/// <summary>Opens a circuit, which stays open for a while, so it gets a host of its own.</summary>
public class CircuitBreakerApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private HttpClient Client => factory.ClientFor(HubApiFactory.ClientKey);

    private HttpClient Admin => factory.ClientFor(HubApiFactory.AdminKey);

    private static JsonNode Outcome(JsonNode body, string supplier) =>
        body["summary"]!["suppliers"]!.AsArray().Single(s => s!["supplier"]!.GetValue<string>() == supplier)!;

    [Fact]
    public async Task Repeated_failures_open_the_circuit_which_the_supplier_listing_reports()
    {
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-c", new { errorRate = 1, errorKind = "unavailable" });
        try
        {
            for (var i = 0; i < 12; i++)
            {
                await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(suppliers: ["supplier-c"]));
            }

            var suppliers = await HubApiFactory.ReadJsonAsync(await Client.GetAsync("/api/v1/suppliers"));
            suppliers.AsArray().Single(s => s!["id"]!.GetValue<string>() == "supplier-c")!["circuit"]!.GetValue<string>().ShouldBe("open");

            var rejected = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(suppliers: ["supplier-c"])));
            Outcome(rejected, "supplier-c")["error"]!["message"]!.GetValue<string>().ShouldContain("open");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-c");
        }
    }
}
