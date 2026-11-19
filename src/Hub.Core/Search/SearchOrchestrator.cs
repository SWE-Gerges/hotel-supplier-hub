using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Hub.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hub.Core;

/// <summary>Per-run overrides. Anything left null falls back to the configured defaults.</summary>
/// <param name="Merge">Merge settings for this run only.</param>
/// <param name="Deadline">Wall-clock budget for the whole run.</param>
/// <param name="SearchId">Client-chosen search id, so the run can be cancelled while it is still in flight.</param>
/// <param name="Replaces">Id of an earlier run of the same owner to cancel before this one starts.</param>
public sealed record SearchRunOptions(MergeOptions? Merge = null, TimeSpan? Deadline = null, string? SearchId = null, string? Replaces = null);

/// <summary>
/// Fans a search out to every eligible supplier, merges what comes back by canonical hotel id, and streams
/// the result as <see cref="SearchUpdate"/>s. A supplier that fails, hangs or is cut off by the deadline is
/// reported and the others carry on; partial results are a normal outcome, not an error.
/// </summary>
public sealed class SearchOrchestrator
{
    private readonly ISupplierGateway _gateway;
    private readonly ITenantSupplierSelector _selector;
    private readonly ICanonicalIdResolver _canonicalIds;
    private readonly ICityCodeResolver _cities;
    private readonly IPricingService _pricing;
    private readonly IOfferStore? _offers;
    private readonly ICancellationPolicyNormalizer? _policies;
    private readonly ISamePlaceGuard? _samePlace;
    private readonly SearchRunRegistry _runs;
    private readonly SearchOptions _defaults;
    private readonly TimeProvider _time;
    private readonly ILogger<SearchOrchestrator> _logger;

    public SearchOrchestrator(
        ISupplierGateway gateway,
        ITenantSupplierSelector selector,
        ICanonicalIdResolver canonicalIds,
        ICityCodeResolver cities,
        IPricingService pricing,
        SearchRunRegistry runs,
        IOptions<SearchOptions> options,
        TimeProvider timeProvider,
        ILogger<SearchOrchestrator> logger,
        IOfferStore? offers = null,
        ICancellationPolicyNormalizer? policies = null,
        ISamePlaceGuard? samePlace = null)
    {
        _samePlace = samePlace;
        _offers = offers;
        _policies = policies;
        _gateway = gateway;
        _selector = selector;
        _canonicalIds = canonicalIds;
        _cities = cities;
        _pricing = pricing;
        _runs = runs;
        _defaults = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs a search and streams its updates. Nothing happens until enumeration starts; validation, run-id
    /// conflicts and ownership refusals surface from the first <c>MoveNextAsync</c>. Stopping enumeration
    /// cancels the run and releases its resources.
    /// </summary>
    public IAsyncEnumerable<SearchUpdate> SearchAsync(
        HotelSearchRequest request, SearchOwner owner, CancellationToken cancellationToken = default) =>
        SearchAsync(request, owner, new SearchRunOptions(), cancellationToken);

    public async IAsyncEnumerable<SearchUpdate> SearchAsync(
        HotelSearchRequest request,
        SearchOwner owner,
        SearchRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var session = Start(request, owner, options, cancellationToken);
        await foreach (var update in session.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>Runs a search to completion (or to its deadline) and returns the merged hotels with per-supplier outcomes.</summary>
    public async Task<SearchResult> SearchToCompletionAsync(
        HotelSearchRequest request,
        SearchOwner owner,
        SearchRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var session = Start(request, owner, options ?? new SearchRunOptions(), cancellationToken);

        SearchSummary? summary = null;
        await foreach (var update in session.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (update is Completed completed)
            {
                summary = completed.Summary;
            }
        }

        var hotels = session.Merger.Snapshot();
        if (request.MaxResults is { } max && hotels.Count > max)
        {
            hotels = hotels.Take(max).ToArray();
        }

        return new SearchResult(
            session.Run.Id,
            summary ?? throw new InvalidOperationException("The search ended without a completion event."),
            hotels);
    }

    private Session Start(HotelSearchRequest request, SearchOwner owner, SearchRunOptions options, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidSearchRequestException(errors);
        }

        var merge = options.Merge ?? _defaults.ToMergeOptions();
        var mergeErrors = merge.Validate().ToArray();
        if (mergeErrors.Length > 0)
        {
            throw new InvalidSearchRequestException(new Dictionary<string, string[]> { ["MergeOptions"] = mergeErrors });
        }

        var deadline = options.Deadline ?? _defaults.GlobalDeadline;
        if (deadline <= TimeSpan.Zero)
        {
            throw new InvalidSearchRequestException(new Dictionary<string, string[]> { ["Deadline"] = ["Deadline must be positive."] });
        }

        var suppliers = _selector.Select(owner.Tenant, request.Suppliers);
        var run = _runs.Start(owner, options.SearchId, options.Replaces);

        CancellationTokenSource? deadlineSource = null;
        CancellationTokenSource? workSource = null;
        CancellationTokenSource? emitSource = null;
        try
        {
            deadlineSource = new CancellationTokenSource(deadline, _time);
            workSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken, run.Token, deadlineSource.Token);
            emitSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

            var channel = Channel.CreateBounded<SearchUpdate>(new BoundedChannelOptions(_defaults.UpdateChannelCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var session = new Session(
                _runs, run, owner, request, merge, suppliers, new HotelMerger(run.Id, merge, suppliers, _samePlace),
                channel, deadlineSource, workSource, emitSource, callerToken, _time.GetTimestamp());
            session.Pump = Task.Run(() => PumpAsync(session), CancellationToken.None);
            return session;
        }
        catch
        {
            emitSource?.Dispose();
            workSource?.Dispose();
            deadlineSource?.Dispose();
            _runs.Complete(run);
            throw;
        }
    }

    private async Task PumpAsync(Session session)
    {
        try
        {
            var tasks = session.Suppliers
                .Select(supplier => Task.Run(() => RunSupplierAsync(session, supplier), CancellationToken.None))
                .ToArray();
            var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

            var completed = new Completed(session.Run.Id, BuildSummary(session, outcomes));
            if (!session.Writer.TryWrite(completed))
            {
                await session.Writer.WriteAsync(completed, session.EmitToken).ConfigureAwait(false);
            }

            session.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Hand the failure to whoever is reading the stream; there is no other caller to throw to.
            session.Writer.TryComplete(ex);
        }
    }

    private async Task<SupplierOutcome> RunSupplierAsync(Session session, SupplierId supplier)
    {
        var started = _time.GetTimestamp();
        var work = session.WorkToken;

        try
        {
            await session.EmitAsync(new SupplierStarted(session.Run.Id, supplier), session.EmitToken).ConfigureAwait(false);

            var codes = await _cities.ResolveAsync(session.Request.Destination, supplier, work).ConfigureAwait(false);
            if (codes is null)
            {
                return await FailAsync(session, supplier, SupplierError.Unsupported(
                    $"Destination '{session.Request.Destination}' is not mapped for supplier '{supplier}'."), started).ConfigureAwait(false);
            }

            var query = new SupplierSearchQuery(session.Request, codes);
            session.Queries[supplier] = query;
            var result = await _gateway
                .SearchAsync(supplier, query, (hotels, token) => OnBatchAsync(session, supplier, hotels, token), work)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                return await FailAsync(session, supplier, result.Error, started).ConfigureAwait(false);
            }

            var elapsed = ElapsedMs(started);
            await session.EmitAsync(new SupplierCompleted(session.Run.Id, supplier, result.Value, elapsed), session.EmitToken).ConfigureAwait(false);
            return new SupplierOutcome(supplier, SupplierOutcomeStatus.Succeeded, result.Value, elapsed, null);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            if (session.DeadlineExpired && !session.Run.IsCancelled && !session.CallerToken.IsCancellationRequested)
            {
                return await FailAsync(session, supplier, SupplierError.Timeout("The search deadline was reached before the supplier finished."), started)
                    .ConfigureAwait(false);
            }

            return new SupplierOutcome(supplier, SupplierOutcomeStatus.Cancelled, 0, ElapsedMs(started), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Search {SearchId}: supplier {Supplier} failed unexpectedly.", session.Run.Id, supplier);
            return await FailAsync(session, supplier, SupplierError.Unknown($"Unexpected failure: {ex.GetType().Name}."), started).ConfigureAwait(false);
        }
    }

    private async Task<SupplierOutcome> FailAsync(Session session, SupplierId supplier, SupplierError error, long started)
    {
        var elapsed = ElapsedMs(started);
        try
        {
            await session.EmitAsync(new SupplierFailed(session.Run.Id, supplier, error, elapsed), session.EmitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The consumer is gone, so nobody is left to hear about the failure.
        }

        return new SupplierOutcome(supplier, SupplierOutcomeStatus.Failed, 0, elapsed, error);
    }

    private async ValueTask OnBatchAsync(Session session, SupplierId supplier, IReadOnlyList<Hotel> hotels, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(session, supplier, hotels, cancellationToken).ConfigureAwait(false);
        await session.Merger.MergeAsync(supplier, prepared, session.EmitAsync, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<Hotel>> PrepareAsync(
        Session session, SupplierId supplier, IReadOnlyList<Hotel> hotels, CancellationToken cancellationToken)
    {
        var bookable = hotels.Where(static h => h.Offers.Count > 0).ToList();
        if (bookable.Count == 0)
        {
            return bookable;
        }

        IReadOnlyDictionary<string, string> canonicalIds = new Dictionary<string, string>();
        if (session.Merge.UseMapping && !session.IdentityLookupFailed)
        {
            var codes = bookable.Select(h => OwnCode(h, supplier)).Distinct(StringComparer.Ordinal).ToArray();
            using var budget = new CancellationTokenSource(_defaults.IdentityLookupTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
            try
            {
                canonicalIds = await _canonicalIds.ResolveHotelsAsync(supplier, codes, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Fail open: an identity store that errors or is too slow must not make hotels disappear from the
                // results. The rest of this search skips the lookup instead of waiting on the same outage again.
                session.IdentityLookupFailed = true;
                _logger.LogWarning(ex, "Search {SearchId}: identity lookup failed for supplier {Supplier}; hotels stay unmapped.", session.Run.Id, supplier);
            }
        }

        var currency = session.Request.Currency;
        var prepared = new List<Hotel>(bookable.Count);
        foreach (var hotel in bookable)
        {
            var offers = new List<RoomOffer>(hotel.Offers.Count);
            foreach (var offer in hotel.Offers)
            {
                if (_pricing.ToSellingPrice(session.Owner.Tenant, supplier, offer.Price, currency) is not { } selling)
                {
                    _logger.LogDebug("Dropping offer {OfferId} priced in {Currency}; the search is in {Requested} and no rate converts them.", offer.OfferId, offer.Price.Currency, currency);
                    continue;
                }

                var priced = offer with { OfferId = OfferIds.Scope(offer.OfferId, session.Request), SellingPrice = selling };
                offers.Add(_policies?.Apply(priced, session.Request.Stay, _time.GetUtcNow()) ?? priced);
            }

            if (offers.Count > 0)
            {
                var canonicalId = canonicalIds.GetValueOrDefault(OwnCode(hotel, supplier));
                prepared.Add(hotel with { CanonicalId = canonicalId, Offers = offers });
                if (_offers is not null && session.Queries.TryGetValue(supplier, out var query))
                {
                    foreach (var offer in offers)
                    {
                        _offers.Save(session.Owner.Tenant, canonicalId, hotel.Name, offer, query);
                    }
                }
            }
        }

        return prepared;
    }

    private static string OwnCode(Hotel hotel, SupplierId supplier) =>
        hotel.Sources.FirstOrDefault(s => s.Supplier == supplier)?.Code
        ?? throw new InvalidOperationException($"Hotel '{hotel.Name}' from '{supplier}' carries no source reference for that supplier.");

    private SearchSummary BuildSummary(Session session, SupplierOutcome[] outcomes)
    {
        var status = SearchStatus.Completed;
        if (session.Run.IsCancelled || session.CallerToken.IsCancellationRequested)
        {
            status = SearchStatus.Cancelled;
        }
        else if (outcomes.Length > 0 && outcomes.Any(static o => o.Status != SupplierOutcomeStatus.Succeeded))
        {
            status = outcomes.Any(static o => o.Status == SupplierOutcomeStatus.Succeeded) ? SearchStatus.Partial : SearchStatus.Failed;
        }

        var hotels = session.Merger.Snapshot();
        return new SearchSummary(status, hotels.Count, hotels.Sum(static h => h.Offers.Count), ElapsedMs(session.StartedAt), outcomes);
    }

    private long ElapsedMs(long startTimestamp) => (long)_time.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private sealed class Session(
        SearchRunRegistry runs,
        SearchRun run,
        SearchOwner owner,
        HotelSearchRequest request,
        MergeOptions merge,
        IReadOnlyList<SupplierId> suppliers,
        HotelMerger merger,
        Channel<SearchUpdate> channel,
        CancellationTokenSource deadline,
        CancellationTokenSource work,
        CancellationTokenSource emit,
        CancellationToken callerToken,
        long startedAt) : IAsyncDisposable
    {
        public SearchRun Run { get; } = run;

        public SearchOwner Owner { get; } = owner;

        public HotelSearchRequest Request { get; } = request;

        public MergeOptions Merge { get; } = merge;

        public IReadOnlyList<SupplierId> Suppliers { get; } = suppliers;

        public HotelMerger Merger { get; } = merger;

        public ChannelWriter<SearchUpdate> Writer => channel.Writer;

        public CancellationToken WorkToken => work.Token;

        public CancellationToken EmitToken => emit.Token;

        public CancellationToken CallerToken { get; } = callerToken;

        public bool DeadlineExpired => deadline.IsCancellationRequested;

        public long StartedAt { get; } = startedAt;

        public Task Pump { get; set; } = Task.CompletedTask;

        public System.Collections.Concurrent.ConcurrentDictionary<SupplierId, SupplierSearchQuery> Queries { get; } = new();

        /// <summary>Set once an identity lookup fails, so later batches of the same search do not wait on the same outage.</summary>
        public volatile bool IdentityLookupFailed;

        public IAsyncEnumerable<SearchUpdate> ReadAsync(CancellationToken cancellationToken) =>
            channel.Reader.ReadAllAsync(cancellationToken);

        public ValueTask EmitAsync(SearchUpdate update, CancellationToken cancellationToken) =>
            channel.Writer.WriteAsync(update, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            // Cancel first: producers blocked on a full channel would otherwise never let the pump finish.
            await emit.CancelAsync().ConfigureAwait(false);
            await work.CancelAsync().ConfigureAwait(false);
            await Pump.ConfigureAwait(false);

            runs.Complete(Run);
            emit.Dispose();
            work.Dispose();
            deadline.Dispose();
        }
    }
}
