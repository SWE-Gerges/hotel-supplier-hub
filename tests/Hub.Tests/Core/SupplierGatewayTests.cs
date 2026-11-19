using System.Runtime.CompilerServices;
using Hub.Abstractions;
using Hub.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SupplierGatewayTests
{
    private static readonly SupplierId Id = TestData.A;

    private static (SupplierGateway Gateway, FakeTimeProvider Time) Create(StubSupplier stub, Action<SupplierResilienceOptions>? configure = null)
    {
        var time = new FakeTimeProvider();
        var options = OrchestratorHarness.FastResilience();
        configure?.Invoke(options.Defaults);
        var registry = new StaticSupplierRegistry([new SupplierEntry(stub, stub)]);
        return (new SupplierGateway(registry, Options.Create(options), time, NullLogger<SupplierGateway>.Instance), time);
    }

    private static ValueTask<Result<int>> RunAsync(
        SupplierGateway gateway,
        Func<IReadOnlyList<Hotel>, CancellationToken, ValueTask>? onBatch = null,
        CancellationToken cancellationToken = default) =>
        gateway.SearchAsync(Id, TestData.Query(), onBatch ?? ((_, _) => ValueTask.CompletedTask), cancellationToken);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(2);
        }
    }

    private static IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> Gated(Task gate, CancellationToken cancellationToken) =>
        GatedCore(gate, cancellationToken);

    private static async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> GatedCore(Task gate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        yield return Result.Ok<IReadOnlyList<Hotel>>([]);
    }

    private static async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> Script(params Result<IReadOnlyList<Hotel>>[] items)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static Result<IReadOnlyList<Hotel>> Ok(int hotels) =>
        Result.Ok<IReadOnlyList<Hotel>>(Enumerable.Range(0, hotels).Select(i => TestData.Hotel(Id, $"h{i}")).ToArray());

    private static Result<IReadOnlyList<Hotel>> Fail(SupplierErrorKind kind, bool retryable = false) =>
        Result.Fail<IReadOnlyList<Hotel>>(new SupplierError(kind, "boom", retryable));

    [Fact]
    public async Task Delivers_each_batch_as_it_arrives_and_counts_the_hotels()
    {
        var stub = new StubSupplier(Id, (_, _, _) => Script(Ok(2), Ok(1)));
        var (gateway, _) = Create(stub);
        var batches = new List<int>();

        var result = await RunAsync(gateway, (hotels, _) =>
        {
            batches.Add(hotels.Count);
            return ValueTask.CompletedTask;
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(3);
        batches.ShouldBe([2, 1]);
    }

    [Fact]
    public async Task Retries_transient_failures_and_counts_only_the_final_attempt()
    {
        var stub = new StubSupplier(Id, (call, _, _) => call switch
        {
            1 => Script(Ok(1), Fail(SupplierErrorKind.Transient, retryable: true)),
            2 => Script(Fail(SupplierErrorKind.Transient, retryable: true)),
            _ => Script(Ok(2)),
        });
        var (gateway, _) = Create(stub, o => o.MaxAttempts = 3);
        var delivered = 0;

        var result = await RunAsync(gateway, (hotels, _) =>
        {
            delivered += hotels.Count;
            return ValueTask.CompletedTask;
        });

        stub.Calls.ShouldBe(3);
        result.Value.ShouldBe(2, "the count describes the attempt that succeeded, not a running total");
        delivered.ShouldBe(3, "the partial batch of the failed attempt was handed over before the retry");
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt_and_returns_the_failure()
    {
        var stub = new StubSupplier(Id, (_, _, _) => Script(Fail(SupplierErrorKind.Transient, retryable: true)));
        var (gateway, _) = Create(stub, o => o.MaxAttempts = 3);

        var result = await RunAsync(gateway);

        stub.Calls.ShouldBe(3);
        result.Error.Kind.ShouldBe(SupplierErrorKind.Transient);
    }

    [Theory]
    [InlineData(SupplierErrorKind.Unavailable)]
    [InlineData(SupplierErrorKind.InvalidRequest)]
    [InlineData(SupplierErrorKind.Auth)]
    [InlineData(SupplierErrorKind.RateLimited)]
    [InlineData(SupplierErrorKind.Timeout)]
    [InlineData(SupplierErrorKind.Unsupported)]
    [InlineData(SupplierErrorKind.Unknown)]
    public async Task Does_not_retry_failures_that_are_not_retryable(SupplierErrorKind kind)
    {
        var stub = new StubSupplier(Id, (_, _, _) => Script(Fail(kind)));
        var (gateway, _) = Create(stub, o => o.MaxAttempts = 3);

        var result = await RunAsync(gateway);

        stub.Calls.ShouldBe(1);
        result.Error.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Converts_an_unexpected_exception_to_an_Unknown_failure_without_retrying()
    {
        var stub = new StubSupplier(Id, (_, _, _) => throw new InvalidOperationException("secret detail"));
        var (gateway, _) = Create(stub, o => o.MaxAttempts = 3);

        var result = await RunAsync(gateway);

        stub.Calls.ShouldBe(1);
        result.Error.Kind.ShouldBe(SupplierErrorKind.Unknown);
        result.Error.Message.ShouldNotContain("secret detail", Case.Insensitive, "internals do not leak into the error");
    }

    [Fact]
    public async Task An_unknown_supplier_is_Unsupported()
    {
        var (gateway, _) = Create(StubSupplier.Returning(Id));

        var result = await gateway.SearchAsync(TestData.B, TestData.Query(), (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        result.Error.Kind.ShouldBe(SupplierErrorKind.Unsupported);
    }

    [Fact]
    public async Task A_hung_attempt_ends_with_a_Timeout_failure()
    {
        var stub = StubSupplier.Hanging(Id);
        var (gateway, time) = Create(stub, o => o.Timeout = TimeSpan.FromSeconds(5));

        var run = RunAsync(gateway).AsTask();
        await WaitUntilAsync(() => stub.Calls == 1);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await run;

        result.Error.Kind.ShouldBe(SupplierErrorKind.Timeout);
        stub.Cancelled.Task.IsCompleted.ShouldBeTrue("the supplier call was cancelled when the timeout fired");
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation_and_reaches_the_supplier()
    {
        var stub = StubSupplier.Hanging(Id);
        var (gateway, _) = Create(stub);
        using var cts = new CancellationTokenSource();

        var run = RunAsync(gateway, cancellationToken: cts.Token).AsTask();
        await WaitUntilAsync(() => stub.Calls == 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await stub.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_circuit_opens_after_repeated_failures_and_closes_again_once_the_supplier_recovers()
    {
        var stub = new StubSupplier(Id, (call, _, _) => call <= 2 ? Script(Fail(SupplierErrorKind.Transient, retryable: true)) : Script(Ok(1)));
        var (gateway, time) = Create(stub, o =>
        {
            o.CircuitMinimumThroughput = 2;
            o.CircuitFailureRatio = 0.5;
            o.CircuitSamplingDuration = TimeSpan.FromSeconds(10);
            o.CircuitBreakDuration = TimeSpan.FromSeconds(5);
        });

        (await RunAsync(gateway)).IsFailure.ShouldBeTrue();
        (await RunAsync(gateway)).IsFailure.ShouldBeTrue();
        gateway.GetStatus(Id).Circuit.ShouldBe(SupplierCircuitState.Open);

        var rejected = await RunAsync(gateway);
        rejected.Error.Kind.ShouldBe(SupplierErrorKind.Unavailable);
        rejected.Error.Message.ShouldContain("open");
        stub.Calls.ShouldBe(2, "an open circuit does not call the supplier");

        time.Advance(TimeSpan.FromSeconds(6));
        var probe = await RunAsync(gateway);

        probe.IsSuccess.ShouldBeTrue();
        stub.Calls.ShouldBe(3);
        gateway.GetStatus(Id).Circuit.ShouldBe(SupplierCircuitState.Closed);
    }

    [Fact]
    public async Task A_failing_probe_keeps_the_circuit_open()
    {
        var stub = new StubSupplier(Id, (_, _, _) => Script(Fail(SupplierErrorKind.Transient, retryable: true)));
        var (gateway, time) = Create(stub, o =>
        {
            o.CircuitMinimumThroughput = 2;
            o.CircuitSamplingDuration = TimeSpan.FromSeconds(10);
            o.CircuitBreakDuration = TimeSpan.FromSeconds(5);
        });
        await RunAsync(gateway);
        await RunAsync(gateway);

        time.Advance(TimeSpan.FromSeconds(6));
        (await RunAsync(gateway)).Error.Kind.ShouldBe(SupplierErrorKind.Transient);

        gateway.GetStatus(Id).Circuit.ShouldBe(SupplierCircuitState.Open);
        (await RunAsync(gateway)).Error.Kind.ShouldBe(SupplierErrorKind.Unavailable);
    }

    [Fact]
    public async Task Timeouts_count_towards_the_circuit()
    {
        var stub = StubSupplier.Hanging(Id);
        var (gateway, time) = Create(stub, o =>
        {
            o.Timeout = TimeSpan.FromSeconds(1);
            o.CircuitMinimumThroughput = 2;
            o.CircuitSamplingDuration = TimeSpan.FromSeconds(30);
        });

        for (var call = 1; call <= 2; call++)
        {
            var run = RunAsync(gateway).AsTask();
            await WaitUntilAsync(() => stub.Calls == call);
            time.Advance(TimeSpan.FromSeconds(1));
            (await run).Error.Kind.ShouldBe(SupplierErrorKind.Timeout);
        }

        gateway.GetStatus(Id).Circuit.ShouldBe(SupplierCircuitState.Open);
    }

    [Fact]
    public async Task Failures_that_blame_the_caller_do_not_trip_the_circuit()
    {
        var stub = new StubSupplier(Id, (_, _, _) => Script(Fail(SupplierErrorKind.InvalidRequest)));
        var (gateway, _) = Create(stub, o => o.CircuitMinimumThroughput = 2);

        for (var i = 0; i < 6; i++)
        {
            (await RunAsync(gateway)).Error.Kind.ShouldBe(SupplierErrorKind.InvalidRequest);
        }

        stub.Calls.ShouldBe(6);
        gateway.GetStatus(Id).Circuit.ShouldBe(SupplierCircuitState.Closed);
    }

    [Fact]
    public async Task The_bulkhead_caps_in_flight_calls_and_lets_the_rest_wait()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new StubSupplier(Id, (_, _, ct) => Gated(gate.Task, ct));
        var (gateway, _) = Create(stub, o =>
        {
            o.MaxConcurrency = 2;
            o.QueueLimit = 10;
        });

        var runs = Enumerable.Range(0, 6).Select(_ => RunAsync(gateway).AsTask()).ToList();
        await WaitUntilAsync(() => stub.InFlight == 2);
        await Task.Delay(50);

        stub.InFlight.ShouldBe(2, "only two calls run at once");
        gateway.GetStatus(Id).InFlight.ShouldBe(2, "queued callers are not counted as in flight");

        gate.SetResult();
        var results = await Task.WhenAll(runs);

        results.ShouldAllBe(static r => r.IsSuccess);
        stub.MaxInFlight.ShouldBe(2);
        stub.Calls.ShouldBe(6);
    }

    [Fact]
    public async Task Calls_beyond_the_queue_limit_are_rejected_immediately()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new StubSupplier(Id, (_, _, ct) => Gated(gate.Task, ct));
        var (gateway, _) = Create(stub, o =>
        {
            o.MaxConcurrency = 1;
            o.QueueLimit = 1;
        });

        var running = RunAsync(gateway).AsTask();
        await WaitUntilAsync(() => stub.InFlight == 1);
        var queued = RunAsync(gateway).AsTask();
        await Task.Delay(50);

        var rejected = await RunAsync(gateway);

        rejected.Error.Kind.ShouldBe(SupplierErrorKind.RateLimited);
        gate.SetResult();
        (await running).IsSuccess.ShouldBeTrue();
        (await queued).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_queued_call_can_be_cancelled_and_does_not_hold_on_to_a_slot()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new StubSupplier(Id, (_, _, ct) => Gated(gate.Task, ct));
        var (gateway, _) = Create(stub, o =>
        {
            o.MaxConcurrency = 1;
            o.QueueLimit = 5;
        });
        using var cts = new CancellationTokenSource();

        var running = RunAsync(gateway).AsTask();
        await WaitUntilAsync(() => stub.InFlight == 1);
        var waiting = RunAsync(gateway, cancellationToken: cts.Token).AsTask();
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.SetResult();
        (await running).IsSuccess.ShouldBeTrue();
        (await RunAsync(gateway)).IsSuccess.ShouldBeTrue();
        stub.Calls.ShouldBe(2, "the cancelled call never reached the supplier");
    }

    [Fact]
    public async Task Status_reports_a_closed_circuit_before_any_call()
    {
        var (gateway, _) = Create(StubSupplier.Returning(Id));

        gateway.GetStatus(Id).ShouldBe(new SupplierRuntimeStatus(SupplierCircuitState.Closed, 0));
        gateway.GetStatus(TestData.C).ShouldBe(new SupplierRuntimeStatus(SupplierCircuitState.Closed, 0));
        await Task.CompletedTask;
    }
}
