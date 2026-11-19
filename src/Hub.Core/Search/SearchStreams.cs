using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Core;

/// <summary>An update with the position it took in its run's event log. Positions start at 1 and never repeat.</summary>
public sealed record SequencedUpdate(long Id, SearchUpdate Update);

/// <summary>The events a client asked to resume from have already been dropped from the bounded buffer.</summary>
public sealed class EventsEvictedException(long requestedAfter, long firstRetained)
    : Exception($"Events after id {requestedAfter} are no longer buffered; the oldest retained id is {firstRetained}.")
{
    public long RequestedAfter { get; } = requestedAfter;

    public long FirstRetained { get; } = firstRetained;
}

public sealed class StreamingOptions
{
    public const string SectionName = "Hub:Streaming";

    /// <summary>Events kept per run for resuming with Last-Event-ID. Older events are dropped first.</summary>
    public int ReplayBufferSize { get; set; } = 512;

    /// <summary>How long the events of a finished run stay available.</summary>
    public TimeSpan ReplayRetention { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Most finished runs kept for replay; the oldest are dropped first.</summary>
    public int MaxRetainedRuns { get; set; } = 128;

    /// <summary>
    /// How long a run keeps going after its last listener disconnected, so the client can reconnect and resume.
    /// Zero cancels the run as soon as the last listener leaves.
    /// </summary>
    public TimeSpan DisconnectGrace { get; set; } = TimeSpan.Zero;
}

internal sealed class StreamingOptionsValidator : IValidateOptions<StreamingOptions>
{
    public ValidateOptionsResult Validate(string? name, StreamingOptions options)
    {
        var failures = new List<string>();
        if (options.ReplayBufferSize < 1)
        {
            failures.Add("ReplayBufferSize must be at least 1.");
        }

        if (options.ReplayRetention <= TimeSpan.Zero)
        {
            failures.Add("ReplayRetention must be positive.");
        }

        if (options.MaxRetainedRuns < 1)
        {
            failures.Add("MaxRetainedRuns must be at least 1.");
        }

        if (options.DisconnectGrace < TimeSpan.Zero)
        {
            failures.Add("DisconnectGrace cannot be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// The ordered, bounded record of one run's updates. Readers can start anywhere inside the retained window, are handed
/// the buffered events first and then wait for new ones until the run ends.
/// </summary>
public sealed class SearchEventLog(int capacity)
{
    private readonly Lock _gate = new();
    private readonly List<SequencedUpdate> _events = [];
    private TaskCompletionSource _signal = NewSignal();
    private long _firstId = 1;
    private long _lastId;
    private bool _completed;
    private ExceptionDispatchInfo? _fault;

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completed;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    public long Append(SearchUpdate update)
    {
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The event log is already closed.");
            }

            _events.Add(new SequencedUpdate(++_lastId, update));
            if (_events.Count > capacity)
            {
                _events.RemoveAt(0);
                _firstId++;
            }

            Wake();
            return _lastId;
        }
    }

    public void Fail(Exception exception)
    {
        lock (_gate)
        {
            _fault = ExceptionDispatchInfo.Capture(exception);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Wake();
        }
    }

    /// <summary>Throws <see cref="EventsEvictedException"/> when events after <paramref name="afterId"/> are no longer all retained.</summary>
    public void EnsureResumable(long afterId)
    {
        lock (_gate)
        {
            if (afterId + 1 < _firstId)
            {
                throw new EventsEvictedException(afterId, _firstId);
            }
        }
    }

    public async IAsyncEnumerable<SequencedUpdate> ReadAsync(long afterId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = afterId;
        while (true)
        {
            SequencedUpdate[] batch;
            Task signal;
            bool done;
            ExceptionDispatchInfo? fault;
            lock (_gate)
            {
                if (next + 1 < _firstId)
                {
                    throw new EventsEvictedException(next, _firstId);
                }

                batch = _events.Skip((int)(next + 1 - _firstId)).ToArray();
                signal = _signal.Task;
                done = _completed;
                fault = _fault;
            }

            foreach (var item in batch)
            {
                yield return item;
                next = item.Id;
            }

            if (batch.Length > 0)
            {
                continue;
            }

            fault?.Throw();
            if (done)
            {
                yield break;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Wake()
    {
        _signal.TrySetResult();
        _signal = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A running search whose updates are kept in an event log so clients can disconnect, reconnect and resume.</summary>
public sealed class SearchStream
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly TimeSpan _grace;
    private readonly TimeProvider _time;
    private int _listeners;

    internal SearchStream(SearchOwner owner, SearchEventLog log, TimeSpan grace, TimeProvider time)
    {
        _token = _stop.Token;
        Owner = owner;
        Log = log;
        _grace = grace;
        _time = time;
    }

    public SearchOwner Owner { get; }

    public SearchEventLog Log { get; }

    internal DateTimeOffset? FinishedAt { get; set; }

    internal CancellationToken StopToken => _token;

    /// <summary>Registers a connected client. The run is cancelled when the last client leaves and stays away for the grace period.</summary>
    public IDisposable Attach()
    {
        Interlocked.Increment(ref _listeners);
        return new Listener(this);
    }

    internal void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and released its token source; there is nothing left to stop.
        }
    }

    internal void ReleaseResources() => _stop.Dispose();

    private void Detached()
    {
        if (Interlocked.Decrement(ref _listeners) != 0 || Log.IsCompleted)
        {
            return;
        }

        if (_grace <= TimeSpan.Zero)
        {
            Stop();
            return;
        }

        _ = StopAfterGraceAsync();
    }

    private async Task StopAfterGraceAsync()
    {
        try
        {
            await Task.Delay(_grace, _time, _token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Volatile.Read(ref _listeners) == 0 && !Log.IsCompleted)
        {
            Stop();
        }
    }

    private sealed class Listener(SearchStream stream) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                stream.Detached();
            }
        }
    }
}

/// <summary>Starts searches whose updates are buffered, and finds them again by search id for resuming.</summary>
public sealed class SearchStreams(SearchOrchestrator orchestrator, IOptions<StreamingOptions> options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SearchStream> _streams = new(StringComparer.Ordinal);
    private readonly StreamingOptions _options = options.Value;

    public int RetainedCount
    {
        get
        {
            lock (_gate)
            {
                return _streams.Count;
            }
        }
    }

    /// <summary>
    /// Starts the search in the background. Validation and ownership refusals are not thrown here: they reach the first
    /// reader of <see cref="SearchStream.Log"/>, exactly as they reach the first reader of the orchestrator's own stream.
    /// </summary>
    public SearchStream Start(HotelSearchRequest request, SearchOwner owner, SearchRunOptions runOptions)
    {
        var log = new SearchEventLog(_options.ReplayBufferSize);
        var stream = new SearchStream(owner, log, _options.DisconnectGrace, time);
        _ = Task.Run(() => PumpAsync(stream, request, owner, runOptions), CancellationToken.None);
        return stream;
    }

    /// <summary>The stream of a run by id: running, or finished and still inside the retention window.</summary>
    public SearchStream? Find(string searchId)
    {
        lock (_gate)
        {
            Purge();
            return _streams.GetValueOrDefault(searchId);
        }
    }

    private async Task PumpAsync(SearchStream stream, HotelSearchRequest request, SearchOwner owner, SearchRunOptions runOptions)
    {
        string? id = null;
        try
        {
            await foreach (var update in orchestrator.SearchAsync(request, owner, runOptions, stream.StopToken).ConfigureAwait(false))
            {
                if (id is null)
                {
                    id = update.SearchId;
                    Register(id, stream);
                }

                stream.Log.Append(update);
            }
        }
        catch (OperationCanceledException) when (stream.StopToken.IsCancellationRequested)
        {
            // Stopped because every listener left; the log ends without a completion event.
        }
        catch (Exception ex)
        {
            stream.Log.Fail(ex);
        }
        finally
        {
            stream.FinishedAt = time.GetUtcNow();
            stream.Log.Complete();
            stream.ReleaseResources();
            if (id is not null)
            {
                lock (_gate)
                {
                    Purge();
                }
            }
        }
    }

    private void Register(string id, SearchStream stream)
    {
        lock (_gate)
        {
            Purge();
            _streams[id] = stream;
        }
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        var finished = _streams.Where(static s => s.Value.FinishedAt is not null).OrderBy(static s => s.Value.FinishedAt).ToList();
        foreach (var (id, stream) in finished)
        {
            if (stream.FinishedAt + _options.ReplayRetention <= now)
            {
                _streams.Remove(id);
            }
        }

        var excess = _streams.Count(static s => s.Value.FinishedAt is not null) - _options.MaxRetainedRuns;
        foreach (var (id, _) in finished.Where(f => _streams.ContainsKey(f.Key)).Take(Math.Max(0, excess)))
        {
            _streams.Remove(id);
        }
    }
}
