using System.Collections.Concurrent;
using Hub.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace Hub.Core;

public enum SupplierCircuitState
{
    Closed,
    Open,
    HalfOpen,
    Isolated,
}

public sealed record SupplierRuntimeStatus(SupplierCircuitState Circuit, int InFlight);

/// <summary>The booking-side calls the hub makes to a supplier through the gateway.</summary>
public enum SupplierOperation
{
    Availability,
    PreBook,
    Book,
    Cancel,
    Retrieve,
}

/// <summary>
/// The only way the orchestrator talks to a supplier. Every supplier gets the same resilience pipeline shape,
/// built from its own options; nothing here knows about a specific supplier.
/// </summary>
public interface ISupplierGateway
{
    /// <summary>
    /// Runs a supplier search through the supplier's pipeline, handing each batch to <paramref name="onBatch"/>
    /// as it arrives. A retry restarts the supplier stream, so batches can be delivered again and consumers must
    /// be idempotent. On success the value is the number of hotels delivered by the final attempt.
    /// Cancellation surfaces as <see cref="OperationCanceledException"/>; every other outcome is a <see cref="Result{T}"/>.
    /// </summary>
    ValueTask<Result<int>> SearchAsync(
        SupplierId supplier,
        SupplierSearchQuery query,
        Func<IReadOnlyList<Hotel>, CancellationToken, ValueTask> onBatch,
        CancellationToken cancellationToken);

    SupplierRuntimeStatus GetStatus(SupplierId supplier);

    /// <summary>
    /// Runs one booking-side call through the supplier's pipeline: a time budget and the supplier's concurrency limit, and
    /// retries for the read-only operations (availability, prebook, retrieve) when the supplier says the failure is
    /// retryable. A booking or a cancellation is never retried here: whether a lost answer means the call went through is
    /// the caller's question to settle, not the gateway's. A timeout comes back as <see cref="SupplierErrorKind.Timeout"/>.
    /// </summary>
    ValueTask<Result<T>> CallAsync<T>(
        SupplierId supplier,
        SupplierOperation operation,
        Func<SupplierEntry, CancellationToken, ValueTask<Result<T>>> call,
        CancellationToken cancellationToken);
}

internal sealed class SupplierGateway : ISupplierGateway
{
    private readonly ISupplierRegistry _registry;
    private readonly ResilienceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SupplierGateway> _logger;
    private readonly ConcurrentDictionary<SupplierId, Lazy<SupplierPipeline>> _pipelines = new();

    public SupplierGateway(
        ISupplierRegistry registry,
        IOptions<ResilienceOptions> options,
        TimeProvider timeProvider,
        ILogger<SupplierGateway> logger)
    {
        _registry = registry;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public SupplierRuntimeStatus GetStatus(SupplierId supplier)
    {
        if (!_registry.TryGet(supplier, out _))
        {
            return new SupplierRuntimeStatus(SupplierCircuitState.Closed, 0);
        }

        var pipeline = GetPipeline(supplier);
        return new SupplierRuntimeStatus(MapState(pipeline.Circuit.CircuitState), pipeline.InFlight);
    }

    public async ValueTask<Result<int>> SearchAsync(
        SupplierId supplier,
        SupplierSearchQuery query,
        Func<IReadOnlyList<Hotel>, CancellationToken, ValueTask> onBatch,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(supplier, out var entry) || entry.Search is null)
        {
            return Result.Fail<int>(SupplierError.Unsupported($"Supplier '{supplier}' cannot search."));
        }

        var pipeline = GetPipeline(supplier);
        var attempt = new Attempt(pipeline, entry.Search, query, onBatch, _logger, supplier);
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        try
        {
            return await pipeline.Pipeline
                .ExecuteAsync(static (ctx, state) => state.RunAsync(ctx.CancellationToken), context, attempt)
                .ConfigureAwait(false);
        }
        catch (TimeoutRejectedException)
        {
            return Result.Fail<int>(SupplierError.Timeout($"Supplier '{supplier}' did not answer within {pipeline.Settings.Timeout.TotalSeconds:0.##}s."));
        }
        catch (BrokenCircuitException)
        {
            return Result.Fail<int>(SupplierError.Unavailable($"Circuit for supplier '{supplier}' is open."));
        }
        catch (RateLimiterRejectedException)
        {
            return Result.Fail<int>(new SupplierError(
                SupplierErrorKind.RateLimited,
                $"Supplier '{supplier}' is at its concurrency limit and the wait queue is full."));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private readonly ConcurrentDictionary<(SupplierId Supplier, Type Result, bool Retry), object> _callPipelines = new();

    public async ValueTask<Result<T>> CallAsync<T>(
        SupplierId supplier,
        SupplierOperation operation,
        Func<SupplierEntry, CancellationToken, ValueTask<Result<T>>> call,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(supplier, out var entry))
        {
            return Result.Fail<T>(SupplierError.Unsupported($"Supplier '{supplier}' is not registered."));
        }

        var retry = operation is SupplierOperation.Availability or SupplierOperation.PreBook or SupplierOperation.Retrieve;
        var pipeline = (ResiliencePipeline<Result<T>>)_callPipelines.GetOrAdd((supplier, typeof(T), retry), _ => BuildCallPipeline<T>(entry, retry));
        var timeout = _options.Resolve(entry.Descriptor).Timeout;
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        try
        {
            return await pipeline
                .ExecuteAsync(static async (ctx, state) => await RunCallAsync(state.Entry, state.Call, state.Logger, state.Supplier, ctx.CancellationToken).ConfigureAwait(false), context, (Entry: entry, Call: call, Logger: _logger, Supplier: supplier))
                .ConfigureAwait(false);
        }
        catch (TimeoutRejectedException)
        {
            return Result.Fail<T>(SupplierError.Timeout($"Supplier '{supplier}' did not answer within {timeout.TotalSeconds:0.##}s."));
        }
        catch (RateLimiterRejectedException)
        {
            return Result.Fail<T>(new SupplierError(SupplierErrorKind.RateLimited, $"Supplier '{supplier}' is at its concurrency limit and the wait queue is full."));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private static async ValueTask<Result<T>> RunCallAsync<T>(
        SupplierEntry entry,
        Func<SupplierEntry, CancellationToken, ValueTask<Result<T>>> call,
        ILogger logger,
        SupplierId supplier,
        CancellationToken cancellationToken)
    {
        try
        {
            return await call(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Supplier {Supplier} threw an unexpected exception during a booking-side call; converting to an Unknown failure.", supplier);
            return Result.Fail<T>(SupplierError.Unknown($"Unexpected failure: {ex.GetType().Name}."));
        }
    }

    private ResiliencePipeline<Result<T>> BuildCallPipeline<T>(SupplierEntry entry, bool retry)
    {
        var settings = _options.Resolve(entry.Descriptor);
        var builder = new ResiliencePipelineBuilder<Result<T>> { TimeProvider = _timeProvider };
        if (retry && settings.MaxAttempts > 1)
        {
            builder.AddRetry(new RetryStrategyOptions<Result<T>>
            {
                MaxRetryAttempts = settings.MaxAttempts - 1,
                Delay = settings.RetryBaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = settings.RetryJitter,
                ShouldHandle = static args => ValueTask.FromResult(
                    args.Outcome.Exception is null && args.Outcome.Result.IsFailure && args.Outcome.Result.Error.Retryable),
            });
        }

        return builder.AddConcurrencyLimiter(settings.MaxConcurrency, settings.QueueLimit).AddTimeout(settings.Timeout).Build();
    }

    private SupplierPipeline GetPipeline(SupplierId supplier) =>
        _pipelines.GetOrAdd(supplier, id => new Lazy<SupplierPipeline>(() => Build(id))).Value;

    private SupplierPipeline Build(SupplierId supplier)
    {
        _registry.TryGet(supplier, out var entry);
        var settings = _options.Resolve(entry!.Descriptor);
        var circuit = new CircuitBreakerStateProvider();

        var builder = new ResiliencePipelineBuilder<Result<int>> { TimeProvider = _timeProvider };

        if (settings.MaxAttempts > 1)
        {
            builder.AddRetry(new RetryStrategyOptions<Result<int>>
            {
                MaxRetryAttempts = settings.MaxAttempts - 1,
                Delay = settings.RetryBaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = settings.RetryJitter,
                ShouldHandle = static args => ValueTask.FromResult(
                    args.Outcome.Exception is null && args.Outcome.Result.IsFailure && args.Outcome.Result.Error.Retryable),
            });
        }

        builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<Result<int>>
            {
                FailureRatio = settings.CircuitFailureRatio,
                MinimumThroughput = settings.CircuitMinimumThroughput,
                SamplingDuration = settings.CircuitSamplingDuration,
                BreakDuration = settings.CircuitBreakDuration,
                StateProvider = circuit,
                ShouldHandle = static args => ValueTask.FromResult(CountsAsCircuitFailure(args.Outcome)),
            })
            .AddConcurrencyLimiter(settings.MaxConcurrency, settings.QueueLimit)
            .AddTimeout(settings.Timeout);

        return new SupplierPipeline(builder.Build(), circuit, settings);
    }

    private static bool CountsAsCircuitFailure(Outcome<Result<int>> outcome)
    {
        if (outcome.Exception is not null)
        {
            return outcome.Exception is TimeoutRejectedException;
        }

        // Faults in the supplier trip the circuit; a bad request or credentials say nothing about its health.
        return outcome.Result.IsFailure && outcome.Result.Error.Kind is
            SupplierErrorKind.Transient or SupplierErrorKind.Timeout or SupplierErrorKind.Unavailable
            or SupplierErrorKind.Unknown or SupplierErrorKind.RateLimited;
    }

    private static SupplierCircuitState MapState(CircuitState state) => state switch
    {
        CircuitState.Open => SupplierCircuitState.Open,
        CircuitState.HalfOpen => SupplierCircuitState.HalfOpen,
        CircuitState.Isolated => SupplierCircuitState.Isolated,
        _ => SupplierCircuitState.Closed,
    };

    private sealed class SupplierPipeline
    {
        private int _inFlight;

        public SupplierPipeline(
            ResiliencePipeline<Result<int>> pipeline, CircuitBreakerStateProvider circuit, ResolvedResilience settings)
        {
            Pipeline = pipeline;
            Circuit = circuit;
            Settings = settings;
        }

        public ResiliencePipeline<Result<int>> Pipeline { get; }

        public CircuitBreakerStateProvider Circuit { get; }

        public ResolvedResilience Settings { get; }

        public int InFlight => Volatile.Read(ref _inFlight);

        public void Enter() => Interlocked.Increment(ref _inFlight);

        public void Exit() => Interlocked.Decrement(ref _inFlight);
    }

    /// <summary>One execution of the pipeline; <see cref="RunAsync"/> is a single attempt at the supplier.</summary>
    private sealed class Attempt(
        SupplierPipeline pipeline,
        IHotelSearch search,
        SupplierSearchQuery query,
        Func<IReadOnlyList<Hotel>, CancellationToken, ValueTask> onBatch,
        ILogger logger,
        SupplierId supplier)
    {
        public async ValueTask<Result<int>> RunAsync(CancellationToken cancellationToken)
        {
            // Counted here, inside the bulkhead, so the figure is calls actually running and not callers queued.
            pipeline.Enter();
            try
            {
                var delivered = 0;
                await foreach (var item in search.SearchAsync(query, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (item.IsFailure)
                    {
                        return Result.Fail<int>(item.Error);
                    }

                    delivered += item.Value.Count;
                    await onBatch(item.Value, cancellationToken).ConfigureAwait(false);
                }

                return Result.Ok(delivered);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Rethrown so the timeout strategy (or the caller) can tell cancellation from failure.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Supplier {Supplier} threw an unexpected exception; converting to an Unknown failure.", supplier);
                return Result.Fail<int>(SupplierError.Unknown($"Unexpected failure: {ex.GetType().Name}."));
            }
            finally
            {
                pipeline.Exit();
            }
        }
    }
}
