using System.Collections.Concurrent;
using Hub.Abstractions;

namespace Hub.Core;

/// <summary>
/// Merges hotels from several suppliers into one hotel per canonical id. The merged result is a pure function
/// of the batches received, whatever order or interleaving they arrive in: static data comes from the
/// highest-priority supplier, offers are sorted cheapest first, and sources are ordered by supplier priority.
/// Batches that touch the same hotel are serialised with per-key async locks taken in one global order.
/// </summary>
public sealed class HotelMerger
{
    private readonly string _runId;
    private readonly MergeOptions _options;
    private readonly Dictionary<SupplierId, int> _rank;
    private readonly ConcurrentDictionary<string, HotelState> _states = new(StringComparer.Ordinal);
    private readonly KeyedAsyncLock _locks = new();
    private readonly ISamePlaceGuard? _samePlace;

    /// <param name="runId">Stamped on the updates this merger emits.</param>
    /// <param name="options">Settings for this run only.</param>
    /// <param name="supplierPriority">Suppliers in priority order; the first listed supplier's static data wins.</param>
    public HotelMerger(string runId, MergeOptions options, IReadOnlyList<SupplierId> supplierPriority, ISamePlaceGuard? samePlace = null)
    {
        _samePlace = samePlace;
        _runId = runId;
        _options = options;
        _rank = new Dictionary<SupplierId, int>();
        for (var i = 0; i < supplierPriority.Count; i++)
        {
            _rank.TryAdd(supplierPriority[i], i);
        }
    }

    public int HotelCount => _states.Count;

    internal int ActiveLockCount => _locks.ActiveKeyCount;

    /// <summary>
    /// Folds a supplier batch into the merged state. Updates are handed to <paramref name="emit"/> while the
    /// affected hotels are still locked, which keeps the updates of any one hotel in the order they happened.
    /// </summary>
    public async ValueTask MergeAsync(
        SupplierId supplier,
        IReadOnlyList<Hotel> hotels,
        Func<SearchUpdate, CancellationToken, ValueTask> emit,
        CancellationToken cancellationToken)
    {
        if (hotels.Count == 0)
        {
            return;
        }

        var byKey = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var hotel in hotels)
        {
            var own = hotel.Sources.FirstOrDefault(s => s.Supplier == supplier)
                      ?? throw new ArgumentException($"Hotel '{hotel.Name}' has no source for supplier '{supplier}'.", nameof(hotels));
            var canonicalId = _options.UseMapping ? hotel.CanonicalId : null;
            var key = canonicalId ?? $"~{supplier}|{own.Code}";

            if (!byKey.TryGetValue(key, out var group))
            {
                byKey[key] = group = new Group(canonicalId);
            }

            group.Items.Add((own.Code, hotel));
        }

        using var lease = await _locks.AcquireAsync(byKey.Keys, cancellationToken).ConfigureAwait(false);

        var added = new List<Hotel>();
        var updates = new List<HotelUpdated>();
        foreach (var key in byKey.Keys.Order(StringComparer.Ordinal))
        {
            var group = byKey[key];
            var state = _states.GetOrAdd(key, static (_, canonical) => new HotelState(canonical), group.CanonicalId);

            foreach (var (code, hotel) in group.Items)
            {
                state.Absorb(supplier, code, hotel);
            }

            var previous = state.Merged;
            var next = Compose(state);
            state.Merged = next;

            if (previous is null)
            {
                added.Add(next);
            }
            else if (ShouldReport(previous, next))
            {
                updates.Add(new HotelUpdated(_runId, next, Cheapest(previous), Cheapest(next)));
            }
        }

        if (added.Count > 0)
        {
            await emit(new HotelsAdded(_runId, added), cancellationToken).ConfigureAwait(false);
        }

        foreach (var update in updates)
        {
            await emit(update, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The merged hotels, cheapest first, ties broken by name and then key for a stable order.</summary>
    public IReadOnlyList<Hotel> Snapshot() =>
        _states
            .Select(static pair => (pair.Key, Hotel: pair.Value.Merged))
            .Where(static x => x.Hotel is not null)
            .OrderBy(static x => Cheapest(x.Hotel!)?.Amount ?? decimal.MaxValue)
            .ThenBy(static x => x.Hotel!.Name, StringComparer.Ordinal)
            .ThenBy(static x => x.Key, StringComparer.Ordinal)
            .Select(static x => x.Hotel!)
            .ToArray();

    private Hotel Compose(HotelState state)
    {
        var contributions = state.Contributions
            .OrderBy(pair => RankOf(pair.Key))
            .ThenBy(static pair => pair.Key.Value, StringComparer.Ordinal)
            .ToArray();

        var primary = contributions[0].Value.Static;

        var offers = contributions
            .SelectMany(static pair => pair.Value.Offers.Values)
            .OrderBy(static o => o.SellingPrice.Amount)
            .ThenBy(o => RankOf(o.Supplier))
            .ThenBy(static o => o.OfferId, StringComparer.Ordinal)
            .Take(_options.MaxOffersPerHotel)
            .Select(o => o with { GroupKey = RoomGroupKey.Create(primary.Name, o.RoomName, o.Board) })
            .ToArray();

        var sources = contributions
            .SelectMany(static pair => pair.Value.Sources.Order(SourceComparer.Instance))
            .Distinct()
            .ToArray();

        var overlay = ChooseOverlay(state, contributions, primary);
        return new Hotel
        {
            CanonicalId = state.CanonicalId,
            Name = overlay?.Name ?? primary.Name,
            Stars = primary.Stars,
            Location = primary.Location,
            Address = overlay?.Address ?? primary.Address,
            Offers = offers,
            Sources = sources,
            Overlay = overlay,
        };
    }

    /// <summary>
    /// The first supplier, in priority order, whose localised overlay is the same place as the hotel. An overlay that is not
    /// leaves the hotel's own name and address alone; each such rejection is counted and logged once per hotel and supplier.
    /// </summary>
    private LocalizedOverlay? ChooseOverlay(HotelState state, KeyValuePair<SupplierId, Contribution>[] contributions, Hotel primary)
    {
        if (_samePlace is null)
        {
            return null;
        }

        foreach (var (supplier, contribution) in contributions)
        {
            if (contribution.Static.Overlay is not { } overlay)
            {
                continue;
            }

            var verdict = _samePlace.Evaluate(primary.Name, primary.Location, overlay);
            if (verdict.SamePlace)
            {
                return overlay;
            }

            if (state.RejectedOverlays.Add(supplier))
            {
                _samePlace.RecordRejection(supplier, primary.Name, verdict);
            }
        }

        return null;
    }

    private int RankOf(SupplierId supplier) => _rank.GetValueOrDefault(supplier, int.MaxValue);

    private bool ShouldReport(Hotel previous, Hotel next)
    {
        var previousIds = previous.Offers.Select(static o => o.OfferId).ToHashSet(StringComparer.Ordinal);
        var structural = next.Offers.Any(o => !previousIds.Contains(o.OfferId))
                         || !previous.Sources.SequenceEqual(next.Sources)
                         || previous.Name != next.Name
                         || previous.Stars != next.Stars
                         || previous.Location != next.Location
                         || previous.Address != next.Address
                         || previous.Overlay != next.Overlay;
        if (structural)
        {
            return true;
        }

        var before = Cheapest(previous)?.Amount;
        var after = Cheapest(next)?.Amount;
        return before != after && Math.Abs((after ?? 0m) - (before ?? 0m)) >= _options.PriceChangeThreshold;
    }

    private static Money? Cheapest(Hotel hotel) => hotel.Offers.Count > 0 ? hotel.Offers[0].SellingPrice : null;

    private sealed record Group(string? CanonicalId)
    {
        public List<(string Code, Hotel Hotel)> Items { get; } = [];
    }

    private sealed class HotelState(string? canonicalId)
    {
        private volatile Hotel? _merged;

        public string? CanonicalId { get; } = canonicalId;

        public Dictionary<SupplierId, Contribution> Contributions { get; } = [];

        public HashSet<SupplierId> RejectedOverlays { get; } = [];

        public Hotel? Merged
        {
            get => _merged;
            set => _merged = value;
        }

        public void Absorb(SupplierId supplier, string code, Hotel hotel)
        {
            if (!Contributions.TryGetValue(supplier, out var contribution))
            {
                Contributions[supplier] = contribution = new Contribution();
            }

            contribution.Sources.Add(new SupplierHotelRef(supplier, code));

            // A supplier may list one physical hotel under several codes; the lowest code supplies the static data.
            if (contribution.StaticCode is null || string.CompareOrdinal(code, contribution.StaticCode) < 0)
            {
                contribution.StaticCode = code;
                contribution.Static = hotel;
            }

            foreach (var offer in hotel.Offers)
            {
                contribution.Offers[offer.OfferId] = offer;
            }
        }
    }

    private sealed class Contribution
    {
        public string? StaticCode { get; set; }

        public Hotel Static { get; set; } = null!;

        public Dictionary<string, RoomOffer> Offers { get; } = new(StringComparer.Ordinal);

        public HashSet<SupplierHotelRef> Sources { get; } = [];
    }

    private sealed class SourceComparer : IComparer<SupplierHotelRef>
    {
        public static readonly SourceComparer Instance = new();

        public int Compare(SupplierHotelRef? x, SupplierHotelRef? y) => string.CompareOrdinal(x?.Code, y?.Code);
    }
}
