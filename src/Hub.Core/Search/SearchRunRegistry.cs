namespace Hub.Core;

public enum CancelOutcome
{
    Cancelled,
    NotFound,
    NotOwner,
}

/// <summary>A search in flight. Cancelling it stops the suppliers and ends the update stream.</summary>
public sealed class SearchRun : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();

    internal SearchRun(string id, SearchOwner owner, DateTimeOffset startedAt)
    {
        Id = id;
        Owner = owner;
        StartedAt = startedAt;
    }

    public string Id { get; }

    public SearchOwner Owner { get; }

    public DateTimeOffset StartedAt { get; }

    public CancellationToken Token => _cancellation.Token;

    public bool IsCancelled => _cancellation.IsCancellationRequested;

    internal void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and was cleaned up concurrently; there is nothing left to cancel.
        }
    }

    public void Dispose() => _cancellation.Dispose();
}

public sealed class SearchRunAccessDeniedException(string runId)
    : Exception($"Search run '{runId}' belongs to a different owner.")
{
    public string RunId { get; } = runId;
}

public sealed class SearchRunConflictException(string runId)
    : Exception($"A search run with id '{runId}' is already active.")
{
    public string RunId { get; } = runId;
}

public sealed class InvalidSearchRequestException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("The search request is invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Tracks active search runs so they can be cancelled, replaced, and cleaned up.</summary>
public sealed class SearchRunRegistry(TimeProvider timeProvider)
{
    public const int MaxRunIdLength = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SearchRun> _runs = new(StringComparer.Ordinal);

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _runs.Count;
            }
        }
    }

    /// <summary>
    /// Registers a run. When <paramref name="replacesRunId"/> names an active run it is cancelled first, but
    /// only if the same owner started it; replacing someone else's run is refused.
    /// </summary>
    /// <exception cref="SearchRunAccessDeniedException">The run to replace belongs to a different owner.</exception>
    /// <exception cref="SearchRunConflictException">The requested run id is already active.</exception>
    public SearchRun Start(SearchOwner owner, string? runId = null, string? replacesRunId = null)
    {
        if (runId is not null && !IsValidRunId(runId))
        {
            throw new ArgumentException(
                $"Run ids use letters, digits, '-' and '_' and are at most {MaxRunIdLength} characters.", nameof(runId));
        }

        lock (_gate)
        {
            if (replacesRunId is not null && _runs.TryGetValue(replacesRunId, out var previous))
            {
                if (previous.Owner != owner)
                {
                    throw new SearchRunAccessDeniedException(replacesRunId);
                }

                _runs.Remove(replacesRunId);
                previous.Cancel();
            }

            var id = runId ?? Guid.NewGuid().ToString("N");
            if (_runs.ContainsKey(id))
            {
                throw new SearchRunConflictException(id);
            }

            var run = new SearchRun(id, owner, timeProvider.GetUtcNow());
            _runs.Add(id, run);
            return run;
        }
    }

    public CancelOutcome Cancel(string runId, SearchOwner requester)
    {
        SearchRun? run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out run))
            {
                return CancelOutcome.NotFound;
            }

            if (run.Owner != requester)
            {
                return CancelOutcome.NotOwner;
            }
        }

        run.Cancel();
        return CancelOutcome.Cancelled;
    }

    /// <summary>Removes a finished run. Safe to call for a run that was already replaced or removed.</summary>
    public void Complete(SearchRun run)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(run.Id, out var current) && ReferenceEquals(current, run))
            {
                _runs.Remove(run.Id);
            }
        }

        run.Dispose();
    }

    public static bool IsValidRunId(string value) =>
        value.Length is >= 1 and <= MaxRunIdLength
        && value.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');
}
