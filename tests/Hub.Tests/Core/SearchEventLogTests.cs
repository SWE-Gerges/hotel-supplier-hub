using Hub.Abstractions;
using Hub.Core;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SearchEventLogTests
{
    private static readonly SupplierId A = SupplierId.Parse("supplier-a");

    private static SearchUpdate Update(int n) => new SupplierStarted($"run-{n}", A);

    private static async Task<List<SequencedUpdate>> ReadAllAsync(SearchEventLog log, long after = 0)
    {
        var all = new List<SequencedUpdate>();
        await foreach (var item in log.ReadAsync(after, CancellationToken.None))
        {
            all.Add(item);
        }

        return all;
    }

    [Fact]
    public async Task Events_are_numbered_from_one_and_replayed_in_order()
    {
        var log = new SearchEventLog(10);
        for (var i = 1; i <= 4; i++)
        {
            log.Append(Update(i));
        }

        log.Complete();

        (await ReadAllAsync(log)).Select(static e => e.Id).ShouldBe([1L, 2, 3, 4]);
        (await ReadAllAsync(log, after: 2)).Select(static e => e.Id).ShouldBe([3L, 4]);
        (await ReadAllAsync(log, after: 4)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_buffer_is_bounded_and_resuming_from_a_dropped_event_is_refused()
    {
        var log = new SearchEventLog(3);
        for (var i = 1; i <= 5; i++)
        {
            log.Append(Update(i));
        }

        log.Complete();

        log.Count.ShouldBe(3);
        (await ReadAllAsync(log, after: 2)).Select(static e => e.Id).ShouldBe([3L, 4, 5]);
        log.EnsureResumable(2);
        var evicted = Should.Throw<EventsEvictedException>(() => log.EnsureResumable(1));
        evicted.FirstRetained.ShouldBe(3);
        await Should.ThrowAsync<EventsEvictedException>(async () => await ReadAllAsync(log));
    }

    [Fact]
    public async Task A_reader_replays_the_buffer_and_then_waits_for_live_events_until_the_log_completes()
    {
        var log = new SearchEventLog(10);
        log.Append(Update(1));
        var reader = ReadAllAsync(log);

        await Task.Delay(50);
        reader.IsCompleted.ShouldBeFalse("the run is still going");
        log.Append(Update(2));
        log.Append(Update(3));
        log.Complete();

        (await reader).Select(static e => e.Id).ShouldBe([1L, 2, 3]);
    }

    [Fact]
    public async Task Concurrent_readers_each_see_every_event_once()
    {
        var log = new SearchEventLog(1000);
        var readers = Enumerable.Range(0, 4).Select(_ => ReadAllAsync(log)).ToArray();

        for (var i = 1; i <= 200; i++)
        {
            log.Append(Update(i));
            if (i % 50 == 0)
            {
                await Task.Yield();
            }
        }

        log.Complete();

        foreach (var reader in readers)
        {
            (await reader).Select(static e => e.Id).ShouldBe(Enumerable.Range(1, 200).Select(static i => (long)i));
        }
    }

    [Fact]
    public async Task A_failure_is_rethrown_to_readers_after_the_events_that_preceded_it()
    {
        var log = new SearchEventLog(10);
        log.Append(Update(1));
        log.Fail(new InvalidOperationException("boom"));
        log.Complete();

        var seen = new List<long>();
        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in log.ReadAsync(0, CancellationToken.None))
            {
                seen.Add(item.Id);
            }
        });

        thrown.Message.ShouldBe("boom");
        seen.ShouldBe([1L]);
    }

    [Fact]
    public async Task A_cancelled_reader_stops_waiting()
    {
        var log = new SearchEventLog(10);
        using var cts = new CancellationTokenSource(50);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in log.ReadAsync(0, cts.Token))
            {
            }
        });
    }
}

public class SearchStreamListenerTests
{
    private static SearchStream NewStream(TimeSpan grace, FakeTimeProvider time, SearchEventLog? log = null) =>
        new(new SearchOwner("tenant", "client"), log ?? new SearchEventLog(10), grace, time);

    [Fact]
    public void The_run_is_stopped_when_the_last_listener_leaves_and_the_grace_is_zero()
    {
        var stream = NewStream(TimeSpan.Zero, new FakeTimeProvider());
        var first = stream.Attach();
        var second = stream.Attach();

        first.Dispose();
        stream.StopToken.IsCancellationRequested.ShouldBeFalse("another client is still listening");
        second.Dispose();

        stream.StopToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Disposing_a_listener_twice_counts_once()
    {
        var stream = NewStream(TimeSpan.Zero, new FakeTimeProvider());
        var first = stream.Attach();
        using var second = stream.Attach();

        first.Dispose();
        first.Dispose();

        stream.StopToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task A_listener_who_returns_within_the_grace_keeps_the_run_alive()
    {
        var time = new FakeTimeProvider();
        var stream = NewStream(TimeSpan.FromSeconds(30), time);

        stream.Attach().Dispose();
        time.Advance(TimeSpan.FromSeconds(20));
        using var returned = stream.Attach();
        time.Advance(TimeSpan.FromSeconds(20));
        await Task.Yield();

        stream.StopToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task The_run_is_stopped_when_nobody_returns_within_the_grace()
    {
        var time = new FakeTimeProvider();
        var stream = NewStream(TimeSpan.FromSeconds(30), time);

        stream.Attach().Dispose();
        time.Advance(TimeSpan.FromSeconds(31));
        for (var i = 0; i < 100 && !stream.StopToken.IsCancellationRequested; i++)
        {
            await Task.Delay(10);
        }

        stream.StopToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void A_finished_run_is_not_stopped_when_its_listener_leaves()
    {
        var log = new SearchEventLog(10);
        var stream = NewStream(TimeSpan.Zero, new FakeTimeProvider(), log);
        var listener = stream.Attach();

        log.Complete();
        listener.Dispose();

        stream.StopToken.IsCancellationRequested.ShouldBeFalse();
    }
}
