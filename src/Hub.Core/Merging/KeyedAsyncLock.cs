namespace Hub.Core;

/// <summary>
/// Per-key async mutual exclusion. Several keys are always acquired in one global (ordinal) order, so two callers
/// that need overlapping key sets in opposite order cannot deadlock. Entries are reference-counted and removed
/// as soon as nobody holds or waits for them.
/// </summary>
internal sealed class KeyedAsyncLock
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Keys currently held or awaited; zero when the lock is idle.</summary>
    public int ActiveKeyCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public async ValueTask<Lease> AcquireAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var sorted = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var entries = new Entry[sorted.Length];

        lock (_gate)
        {
            for (var i = 0; i < sorted.Length; i++)
            {
                if (!_entries.TryGetValue(sorted[i], out var entry))
                {
                    entry = new Entry();
                    _entries[sorted[i]] = entry;
                }

                entry.References++;
                entries[i] = entry;
            }
        }

        var acquired = 0;
        try
        {
            for (; acquired < entries.Length; acquired++)
            {
                await entries[acquired].Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            Release(sorted, entries, acquired);
            throw;
        }

        return new Lease(this, sorted, entries);
    }

    private void Release(string[] keys, Entry[] entries, int heldCount)
    {
        for (var i = 0; i < heldCount; i++)
        {
            entries[i].Semaphore.Release();
        }

        lock (_gate)
        {
            for (var i = 0; i < entries.Length; i++)
            {
                if (--entries[i].References == 0)
                {
                    _entries.Remove(keys[i]);
                    entries[i].Semaphore.Dispose();
                }
            }
        }
    }

    internal sealed class Entry
    {
        public int References;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }

    public readonly struct Lease : IDisposable
    {
        private readonly KeyedAsyncLock _owner;
        private readonly string[] _keys;
        private readonly Entry[] _entries;

        internal Lease(KeyedAsyncLock owner, string[] keys, Entry[] entries)
        {
            _owner = owner;
            _keys = keys;
            _entries = entries;
        }

        public void Dispose() => _owner?.Release(_keys, _entries, _entries.Length);
    }
}
