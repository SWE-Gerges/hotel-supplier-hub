using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public sealed class ResumeApiFactory : HubApiFactory
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Hub:Streaming:DisconnectGrace"] = "00:00:20",
        ["Hub:Streaming:ReplayBufferSize"] = "512",
    };
}

public sealed class TinyBufferApiFactory : HubApiFactory
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Hub:Streaming:ReplayBufferSize"] = "4",
        ["Hub:Streaming:DisconnectGrace"] = "00:00:20",
    };
}

public class StreamResumeApiTests(ResumeApiFactory factory) : IClassFixture<ResumeApiFactory>
{
    private static CancellationToken Timeout(int seconds = 60) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private HttpClient Client => factory.ClientFor(HubApiFactory.ClientKey);

    private static Task<HttpResponseMessage> ResumeAsync(HttpClient client, string searchId, string? lastEventId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/searches/{searchId}/events");
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    [Fact]
    public async Task A_finished_run_replays_only_the_events_after_the_last_id()
    {
        var ct = Timeout();
        using var first = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "replay-after"), ct);
        var all = (await Sse.ReadAllAsync(first, ct)).Where(static m => m.Comment is null).ToList();

        using var resumed = await ResumeAsync(Client, "replay-after", "5", ct);
        var replayed = (await Sse.ReadAllAsync(resumed, ct)).Where(static m => m.Comment is null).ToList();

        resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        resumed.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        replayed.Select(static m => m.Id).ShouldBe(all.Skip(5).Select(static m => m.Id));
        replayed.Select(static m => m.Data).ShouldBe(all.Skip(5).Select(static m => m.Data), "the same events, byte for byte");
        replayed.Last().Event.ShouldBe("completed");
    }

    [Fact]
    public async Task Without_a_last_event_id_the_whole_run_is_replayed()
    {
        var ct = Timeout();
        using var first = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "replay-all"), ct);
        var all = (await Sse.ReadAllAsync(first, ct)).Where(static m => m.Comment is null).ToList();

        using var resumed = await ResumeAsync(Client, "replay-all", null, ct);
        var replayed = (await Sse.ReadAllAsync(resumed, ct)).Where(static m => m.Comment is null).ToList();

        replayed.Select(static m => m.Id).ShouldBe(all.Select(static m => m.Id));
    }

    [Fact]
    public async Task A_dropped_connection_resumes_where_it_stopped_and_continues_live_without_gaps()
    {
        var ct = Timeout();
        var admin = factory.ClientFor(HubApiFactory.AdminKey);
        await HubApiFactory.SetFaultsAsync(admin, "supplier-b", new { minLatencyMs = 400, maxLatencyMs = 400 });
        try
        {
            var received = new List<SseMessage>();
            var first = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "drop-and-resume"), ct);
            using (var drop = new CancellationTokenSource())
            {
                await foreach (var message in Sse.ReadAsync(first, drop.Token))
                {
                    if (message.Comment is not null)
                    {
                        continue;
                    }

                    received.Add(message);
                    if (received.Count == 2)
                    {
                        break;
                    }
                }

                await drop.CancelAsync();
            }

            first.Dispose();

            using var resumed = await ResumeAsync(Client, "drop-and-resume", received[^1].Id, ct);
            var rest = (await Sse.ReadAllAsync(resumed, ct)).Where(static m => m.Comment is null).ToList();

            resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
            var ids = received.Concat(rest).Select(static m => long.Parse(m.Id!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            ids.ShouldBe(Enumerable.Range(1, ids.Count).Select(static i => (long)i), "every event arrived exactly once, in order");
            rest.Last().Event.ShouldBe("completed");
            rest.Last().Json["summary"]!["status"]!.GetValue<string>().ShouldBe("completed", "the search kept running while nobody listened");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(admin, "supplier-b");
        }
    }

    [Fact]
    public async Task An_unknown_search_is_404_with_problem_details()
    {
        var ct = Timeout();

        using var response = await ResumeAsync(Client, "never-existed", "3", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_malformed_last_event_id_is_a_validation_problem()
    {
        var ct = Timeout();
        using var first = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "bad-header"), ct);
        await Sse.ReadAllAsync(first, ct);

        using var response = await ResumeAsync(Client, "bad-header", "abc", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Another_caller_cannot_read_a_search_it_did_not_start()
    {
        var ct = Timeout();
        using var first = await HubApiFactory.OpenStreamAsync(Client, HubApiFactory.SearchBody(searchId: "not-yours"), ct);
        await Sse.ReadAllAsync(first, ct);

        using var response = await ResumeAsync(factory.ClientFor(HubApiFactory.LimitedKey), "not-yours", "1", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resuming_needs_authentication()
    {
        using var response = await factory.CreateClient().GetAsync("/api/v1/searches/anything/events", Timeout());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

public class StreamReplayBufferApiTests(TinyBufferApiFactory factory) : IClassFixture<TinyBufferApiFactory>
{
    private static CancellationToken Timeout(int seconds = 60) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    [Fact]
    public async Task Events_that_fell_out_of_the_bounded_buffer_answer_410_and_the_retained_tail_is_still_served()
    {
        var ct = Timeout();
        var client = factory.ClientFor(HubApiFactory.ClientKey);
        using var first = await HubApiFactory.OpenStreamAsync(client, HubApiFactory.SearchBody(searchId: "tiny"), ct);
        await Sse.ReadAllAsync(first, ct); // a listener this slow may itself be cut off; the run keeps going for the grace period
        while ((await client.DeleteAsync("/api/v1/searches/tiny", ct)).StatusCode != HttpStatusCode.NotFound)
        {
            await Task.Delay(20, ct); // the run is still registered, so it has not finished yet
        }


        var gone = new HttpRequestMessage(HttpMethod.Get, "/api/v1/searches/tiny/events");
        gone.Headers.TryAddWithoutValidation("Last-Event-ID", "1");
        using var evicted = await client.SendAsync(gone, ct);
        evicted.StatusCode.ShouldBe(HttpStatusCode.Gone);
        evicted.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var oldest = (await HubApiFactory.ReadJsonAsync(evicted))["oldestRetainedEventId"]!.GetValue<long>();
        oldest.ShouldBeGreaterThan(2);

        var tail = new HttpRequestMessage(HttpMethod.Get, "/api/v1/searches/tiny/events");
        tail.Headers.TryAddWithoutValidation("Last-Event-ID", (oldest - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var retained = await client.SendAsync(tail, HttpCompletionOption.ResponseHeadersRead, ct);
        var replayed = (await Sse.ReadAllAsync(retained, ct)).Where(static m => m.Comment is null).ToList();

        retained.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayed.Count.ShouldBe(4, "the buffer holds four events");
        replayed.Select(static m => long.Parse(m.Id!, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(Enumerable.Range(0, 4).Select(i => oldest + i));
        replayed.Last().Event.ShouldBe("completed");
    }
}
