using System.Runtime.CompilerServices;
using Hub.Abstractions;
using Hub.Core;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SearchOrchestratorTests
{
    private static readonly SupplierId A = TestData.A;
    private static readonly SupplierId B = TestData.B;
    private static readonly SupplierId C = TestData.C;

    private static TestCanonicalIds SharedHotel() => new(new Dictionary<(SupplierId, string), string>
    {
        [(A, "a1")] = "H-000001",
        [(B, "b1")] = "H-000001",
    });

    private static T Single<T>(IEnumerable<SearchUpdate> updates) where T : SearchUpdate => updates.OfType<T>().ShouldHaveSingleItem();

    [Fact]
    public async Task Merges_suppliers_by_canonical_id_and_reports_every_supplier()
    {
        var harness = new OrchestratorHarness(
            [
                StubSupplier.Returning(A, TestData.Hotel(A, "a1", offers: ("Room X", 100m))),
                StubSupplier.Returning(B, TestData.Hotel(B, "b1", offers: ("Room Y", 90m))),
                StubSupplier.Returning(C, TestData.Hotel(C, "c1", "Other Inn", offers: ("Room Z", 110m))),
            ],
            canonicalIds: SharedHotel());

        var result = await harness.Orchestrator.SearchToCompletionAsync(TestData.Request(), TestData.Owner);

        result.Summary.Status.ShouldBe(SearchStatus.Completed);
        result.Summary.Suppliers.ShouldAllBe(static s => s.Status == SupplierOutcomeStatus.Succeeded);
        result.Hotels.Count.ShouldBe(2);
        var merged = result.Hotels.Single(static h => h.CanonicalId == "H-000001");
        merged.Offers.Select(static o => o.SellingPrice.Amount).ShouldBe([90m, 100m]);
        merged.Sources.Select(static s => s.Supplier).ShouldBe([A, B]);
        result.Hotels[0].Offers[0].SellingPrice.Amount.ShouldBe(90m, "hotels come back cheapest first");
        harness.Runs.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Streams_started_then_results_then_completed_for_each_supplier_and_one_final_summary()
    {
        var harness = new OrchestratorHarness(
            [StubSupplier.Returning(A, TestData.Hotel(A, "a1")), StubSupplier.Returning(B, TestData.Hotel(B, "b1"))],
            canonicalIds: SharedHotel());

        var updates = await harness.RunAsync();

        updates.Last().ShouldBeOfType<Completed>();
        updates.OfType<Completed>().Count().ShouldBe(1);
        foreach (var supplier in new[] { A, B })
        {
            var started = updates.FindIndex(u => u is SupplierStarted s && s.Supplier == supplier);
            var completed = updates.FindIndex(u => u is SupplierCompleted s && s.Supplier == supplier);
            started.ShouldBeGreaterThanOrEqualTo(0);
            completed.ShouldBeGreaterThan(started);
        }

        updates.OfType<HotelsAdded>().Sum(static u => u.Hotels.Count).ShouldBe(1, "both suppliers carry the same canonical hotel");
        updates.OfType<HotelUpdated>().ShouldHaveSingleItem();
        updates.Select(static u => u.SearchId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Applies_the_tenant_markup_to_the_selling_price_and_keeps_the_supplier_price()
    {
        var tenants = new Dictionary<string, TenantConfig>
        {
            [TestData.Owner.Tenant] = new() { EnabledSuppliers = ["supplier-a"], MarkupPercent = 10 },
        };
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, TestData.Hotel(A, "a1", offers: ("Room X", 100m)))], tenants);

        var offer = (await harness.Orchestrator.SearchToCompletionAsync(TestData.Request(), TestData.Owner)).Hotels.Single().Offers.Single();

        offer.Price.Amount.ShouldBe(100m);
        offer.SellingPrice.Amount.ShouldBe(110m);
    }

    [Fact]
    public async Task A_supplier_that_fails_is_reported_and_the_others_still_complete()
    {
        var harness = new OrchestratorHarness(
        [
            StubSupplier.Returning(A, TestData.Hotel(A, "a1")),
            StubSupplier.Failing(B, SupplierError.Unavailable("down for maintenance")),
            StubSupplier.Returning(C, TestData.Hotel(C, "c1", "Other Inn")),
        ]);

        var updates = await harness.RunAsync();

        var failed = Single<SupplierFailed>(updates);
        failed.Supplier.ShouldBe(B);
        failed.Error.Kind.ShouldBe(SupplierErrorKind.Unavailable);
        updates.OfType<SupplierCompleted>().Select(static u => u.Supplier).ShouldBe([A, C], ignoreOrder: true);
        var summary = Single<Completed>(updates).Summary;
        summary.Status.ShouldBe(SearchStatus.Partial);
        summary.HotelCount.ShouldBe(2);
        summary.Suppliers.Single(s => s.Supplier == B).Status.ShouldBe(SupplierOutcomeStatus.Failed);
    }

    [Fact]
    public async Task A_supplier_that_throws_is_isolated_and_reported_as_Unknown()
    {
        var harness = new OrchestratorHarness(
        [
            new StubSupplier(A, (_, _, _) => throw new InvalidOperationException("boom")),
            StubSupplier.Returning(B, TestData.Hotel(B, "b1")),
        ]);

        var updates = await harness.RunAsync();

        Single<SupplierFailed>(updates).Error.Kind.ShouldBe(SupplierErrorKind.Unknown);
        Single<SupplierCompleted>(updates).Supplier.ShouldBe(B);
    }

    [Fact]
    public async Task When_every_supplier_fails_the_search_is_Failed_but_still_a_normal_result()
    {
        var harness = new OrchestratorHarness(
        [
            StubSupplier.Failing(A, SupplierError.Unavailable("x")),
            StubSupplier.Failing(B, SupplierError.InvalidRequest("y")),
        ]);

        var result = await harness.Orchestrator.SearchToCompletionAsync(TestData.Request(), TestData.Owner);

        result.Summary.Status.ShouldBe(SearchStatus.Failed);
        result.Hotels.ShouldBeEmpty();
        result.Summary.Suppliers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_hung_supplier_does_not_block_the_others_and_the_deadline_returns_partial_results()
    {
        var hung = StubSupplier.Hanging(B);
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, TestData.Hotel(A, "a1")), hung]);

        var started = DateTime.UtcNow;
        var result = await harness.Orchestrator.SearchToCompletionAsync(
            TestData.Request(), TestData.Owner, new SearchRunOptions(Deadline: TimeSpan.FromSeconds(2)));

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
        result.Summary.Status.ShouldBe(SearchStatus.Partial);
        result.Hotels.ShouldHaveSingleItem();
        var outcome = result.Summary.Suppliers.Single(s => s.Supplier == B);
        outcome.Status.ShouldBe(SupplierOutcomeStatus.Failed);
        outcome.Error!.Kind.ShouldBe(SupplierErrorKind.Timeout);
        hung.Cancelled.Task.IsCompleted.ShouldBeTrue("the hung call was cancelled at the deadline");
        harness.Runs.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_deadline_is_reported_in_the_event_stream_and_leaves_finished_suppliers_untouched()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, TestData.Hotel(A, "a1")), StubSupplier.Hanging(B)], time: time);
        var collector = new UpdateCollector();
        collector.Start(harness.Orchestrator.SearchAsync(
            TestData.Request(), TestData.Owner, new SearchRunOptions(Deadline: TimeSpan.FromSeconds(3)), CancellationToken.None));
        await collector.WaitForAsync<SupplierCompleted>(u => u.Supplier == A);

        time.Advance(TimeSpan.FromSeconds(3));
        var updates = await collector.DrainAsync();

        var failed = Single<SupplierFailed>(updates);
        failed.Supplier.ShouldBe(B);
        failed.Error.Kind.ShouldBe(SupplierErrorKind.Timeout);
        updates.OfType<HotelsAdded>().Sum(static u => u.Hotels.Count).ShouldBe(1);
        updates.Last().ShouldBeOfType<Completed>().Summary.Status.ShouldBe(SearchStatus.Partial);
    }

    [Fact]
    public async Task A_supplier_the_tenant_has_not_enabled_is_skipped_without_error()
    {
        var disabled = StubSupplier.Returning(B, TestData.Hotel(B, "b1"));
        var tenants = new Dictionary<string, TenantConfig> { [TestData.Owner.Tenant] = new() { EnabledSuppliers = ["supplier-a"] } };
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, TestData.Hotel(A, "a1")), disabled], tenants);

        var updates = await harness.RunAsync();

        disabled.Calls.ShouldBe(0);
        updates.OfType<SupplierStarted>().ShouldHaveSingleItem().Supplier.ShouldBe(A);
        updates.OfType<SupplierFailed>().ShouldBeEmpty();
        Single<Completed>(updates).Summary.Status.ShouldBe(SearchStatus.Completed);
    }

    [Fact]
    public async Task The_request_can_narrow_the_suppliers_but_never_widen_them_beyond_the_tenant()
    {
        var stubs = new[] { StubSupplier.Returning(A, TestData.Hotel(A, "a1")), StubSupplier.Returning(B, TestData.Hotel(B, "b1")), StubSupplier.Returning(C, TestData.Hotel(C, "c1")) };
        var tenants = new Dictionary<string, TenantConfig> { [TestData.Owner.Tenant] = new() { EnabledSuppliers = ["supplier-a", "supplier-b"] } };
        var harness = new OrchestratorHarness(stubs, tenants);

        await harness.RunAsync(TestData.Request(suppliers: [B, C]));

        stubs[0].Calls.ShouldBe(0, "not requested");
        stubs[1].Calls.ShouldBe(1);
        stubs[2].Calls.ShouldBe(0, "requested, but the tenant does not have it");
    }

    [Fact]
    public async Task An_unknown_tenant_gets_an_empty_but_complete_search()
    {
        var stub = StubSupplier.Returning(A, TestData.Hotel(A, "a1"));
        var harness = new OrchestratorHarness([stub]);

        var updates = await harness.RunAsync(owner: new SearchOwner("ghost-tenant", "client-1"));

        var completed = updates.ShouldHaveSingleItem().ShouldBeOfType<Completed>();
        completed.Summary.Status.ShouldBe(SearchStatus.Completed);
        completed.Summary.HotelCount.ShouldBe(0);
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_destination_the_supplier_has_no_code_for_is_reported_as_Unsupported_without_calling_it()
    {
        var stub = StubSupplier.Returning(A, TestData.Hotel(A, "a1"));
        var harness = new OrchestratorHarness([stub]);

        var updates = await harness.RunAsync(TestData.Request() with { Destination = "city-unknown" });

        Single<SupplierFailed>(updates).Error.Kind.ShouldBe(SupplierErrorKind.Unsupported);
        stub.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_the_run_stops_the_suppliers_and_ends_with_a_cancelled_summary()
    {
        var hung = StubSupplier.Hanging(B);
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, TestData.Hotel(A, "a1")), hung]);
        var collector = new UpdateCollector();
        collector.Start(harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, CancellationToken.None));

        var finished = await collector.WaitForAsync<SupplierCompleted>();
        harness.Runs.Cancel(finished.SearchId, TestData.Owner).ShouldBe(CancelOutcome.Cancelled);
        var updates = await collector.DrainAsync();

        var summary = updates.OfType<Completed>().ShouldHaveSingleItem().Summary;
        summary.Status.ShouldBe(SearchStatus.Cancelled);
        summary.Suppliers.Single(s => s.Supplier == B).Status.ShouldBe(SupplierOutcomeStatus.Cancelled);
        updates.OfType<SupplierFailed>().ShouldBeEmpty("a cancelled supplier is not a failed one");
        hung.Cancelled.Task.IsCompleted.ShouldBeTrue();
        harness.Runs.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_with_the_callers_token_ends_enumeration_and_cleans_up()
    {
        var hung = StubSupplier.Hanging(A);
        var harness = new OrchestratorHarness([hung]);
        using var cts = new CancellationTokenSource();

        var run = harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, cts.Token).ToListAsync(cts.Token);
        await Task.Delay(100);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await hung.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Runs.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Walking_away_from_a_stream_cancels_the_search_even_when_suppliers_are_blocked_on_back_pressure()
    {
        var chatty = new StubSupplier(A, (_, _, ct) => ManyBatches(ct));
        var harness = new OrchestratorHarness([chatty], search: new SearchOptions { UpdateChannelCapacity = 1, GlobalDeadline = TimeSpan.FromSeconds(30) });

        await using (var updates = harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, CancellationToken.None).GetAsyncEnumerator())
        {
            (await updates.MoveNextAsync()).ShouldBeTrue();
            await WaitUntilAsync(() => chatty.Calls == 1);
            await Task.Delay(150); // long enough for the one-slot channel to fill and the supplier to block on it
        }

        chatty.Cancelled.Task.IsCompleted.ShouldBeTrue("disposing the stream cancelled the supplier");
        chatty.InFlight.ShouldBe(0);
        harness.Runs.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Starting_with_replaces_cancels_the_previous_search_of_the_same_owner()
    {
        var hung = StubSupplier.Hanging(A);
        var harness = new OrchestratorHarness([hung]);
        var first = new UpdateCollector();
        first.Start(harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, new SearchRunOptions(SearchId: "run-1"), CancellationToken.None));
        await first.WaitForAsync<SupplierStarted>();

        var second = new UpdateCollector();
        second.Start(harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, new SearchRunOptions(SearchId: "run-2", Replaces: "run-1"), CancellationToken.None));
        await second.WaitForAsync<SupplierStarted>();

        var firstUpdates = await first.DrainAsync();
        firstUpdates.OfType<Completed>().Single().Summary.Status.ShouldBe(SearchStatus.Cancelled);
        harness.Runs.ActiveCount.ShouldBe(1, "only the replacement is still running");
        harness.Runs.Cancel("run-2", TestData.Owner).ShouldBe(CancelOutcome.Cancelled);
        await second.DrainAsync();
        harness.Runs.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Replacing_somebody_elses_search_is_refused_and_leaves_it_running()
    {
        var harness = new OrchestratorHarness([StubSupplier.Hanging(A)]);
        var victim = new UpdateCollector();
        victim.Start(harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, new SearchRunOptions(SearchId: "run-1"), CancellationToken.None));
        await victim.WaitForAsync<SupplierStarted>();
        var intruder = new SearchOwner(TestData.Owner.Tenant, "someone-else");

        await Should.ThrowAsync<SearchRunAccessDeniedException>(() =>
            harness.Orchestrator.SearchAsync(TestData.Request(), intruder, new SearchRunOptions(Replaces: "run-1"), CancellationToken.None).ToListAsync());

        harness.Runs.Cancel("run-1", intruder).ShouldBe(CancelOutcome.NotOwner);
        harness.Runs.ActiveCount.ShouldBe(1);
        harness.Runs.Cancel("run-1", TestData.Owner);
        await victim.DrainAsync();
    }

    [Fact]
    public async Task A_search_id_that_is_already_running_is_refused()
    {
        var harness = new OrchestratorHarness([StubSupplier.Hanging(A)]);
        var first = new UpdateCollector();
        first.Start(harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, new SearchRunOptions(SearchId: "run-1"), CancellationToken.None));
        await first.WaitForAsync<SupplierStarted>();

        await Should.ThrowAsync<SearchRunConflictException>(() =>
            harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, new SearchRunOptions(SearchId: "run-1"), CancellationToken.None).ToListAsync());

        harness.Runs.Cancel("run-1", TestData.Owner);
        await first.DrainAsync();
    }

    [Fact]
    public async Task An_invalid_request_is_refused_before_anything_starts()
    {
        var stub = StubSupplier.Returning(A, TestData.Hotel(A, "a1"));
        var harness = new OrchestratorHarness([stub]);

        var error = await Should.ThrowAsync<InvalidSearchRequestException>(() =>
            harness.Orchestrator.SearchAsync(TestData.Request(currency: "euro"), TestData.Owner, CancellationToken.None).ToListAsync());

        error.Errors.ShouldContainKey("Currency");
        stub.Calls.ShouldBe(0);
        harness.Runs.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_searches_with_different_merge_options_do_not_interfere()
    {
        // Both searches reach the suppliers before either gets an answer, so their merges genuinely overlap.
        IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> WaitForBoth(StubSupplier self, Hotel hotel, CancellationToken ct) => WaitForBothCore(self, hotel, ct);
        StubSupplier? stubA = null;
        StubSupplier? stubB = null;
        stubA = new StubSupplier(A, (_, _, ct) => WaitForBoth(stubA!, TestData.Hotel(A, "a1", offers: [("R1", 100m), ("R2", 110m), ("R3", 120m)]), ct));
        stubB = new StubSupplier(B, (_, _, ct) => WaitForBoth(stubB!, TestData.Hotel(B, "b1", offers: [("R4", 95m)]), ct));
        var harness = new OrchestratorHarness([stubA, stubB], canonicalIds: SharedHotel());

        var tight = harness.Orchestrator.SearchToCompletionAsync(TestData.Request(), TestData.Owner, new SearchRunOptions(Merge: new MergeOptions(MaxOffersPerHotel: 1)));
        var unmapped = harness.Orchestrator.SearchToCompletionAsync(TestData.Request(), TestData.Owner, new SearchRunOptions(Merge: new MergeOptions(UseMapping: false, MaxOffersPerHotel: 10)));
        var results = await Task.WhenAll(tight, unmapped);

        results[0].Hotels.ShouldHaveSingleItem().Offers.ShouldHaveSingleItem().SellingPrice.Amount.ShouldBe(95m);
        results[0].Hotels[0].CanonicalId.ShouldBe("H-000001");
        results[1].Hotels.Count.ShouldBe(2, "mapping was off for this search only");
        results[1].Hotels.ShouldAllBe(static h => h.CanonicalId == null);
        results[1].Hotels.Sum(static h => h.Offers.Count).ShouldBe(4);
    }

    [Fact]
    public async Task A_failing_identity_lookup_fails_open_and_keeps_the_hotels()
    {
        var ids = new TestCanonicalIds { FailWith = () => new InvalidOperationException("identity store down") };
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, TestData.Hotel(A, "a1"))], canonicalIds: ids);

        var updates = await harness.RunAsync();

        updates.OfType<SupplierFailed>().ShouldBeEmpty();
        updates.OfType<HotelsAdded>().Single().Hotels.ShouldHaveSingleItem().CanonicalId.ShouldBeNull();
    }

    [Fact]
    public async Task A_slow_identity_store_is_given_up_on_after_the_lookup_budget_and_not_asked_again_in_that_search()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var ids = new TestCanonicalIds { Before = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        var harness = new OrchestratorHarness(
            [new StubSupplier(A, (_, _, _) => Script(
                Result.Ok<IReadOnlyList<Hotel>>([TestData.Hotel(A, "a1")]),
                Result.Ok<IReadOnlyList<Hotel>>([TestData.Hotel(A, "a2")])))],
            search: new SearchOptions { IdentityLookupTimeout = TimeSpan.FromSeconds(2), GlobalDeadline = TimeSpan.FromMinutes(5) },
            canonicalIds: ids,
            time: time);
        var collector = new UpdateCollector();
        collector.Start(harness.Orchestrator.SearchAsync(TestData.Request(), TestData.Owner, CancellationToken.None));
        await WaitUntilAsync(() => ids.Lookups == 1);

        time.Advance(TimeSpan.FromSeconds(2));
        var updates = await collector.DrainAsync();

        ids.Lookups.ShouldBe(1, "after the first failure this search stops asking");
        updates.OfType<HotelsAdded>().Sum(static u => u.Hotels.Count).ShouldBe(2);
        updates.OfType<SupplierFailed>().ShouldBeEmpty();
        updates.Last().ShouldBeOfType<Completed>().Summary.Status.ShouldBe(SearchStatus.Completed);
    }

    [Fact]
    public async Task Offers_priced_in_another_currency_and_hotels_without_offers_are_left_out()
    {
        var wrongCurrency = TestData.Hotel(A, "a1") with { Offers = [TestData.Offer(A, "a1", "Room X", 100m, currency: "USD")] };
        var empty = TestData.Hotel(A, "a2") with { Offers = [] };
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, wrongCurrency, empty, TestData.Hotel(A, "a3"))]);

        var result = await harness.Orchestrator.SearchToCompletionAsync(TestData.Request(), TestData.Owner);

        result.Hotels.ShouldHaveSingleItem().Sources[0].Code.ShouldBe("a3");
    }

    [Fact]
    public async Task A_retry_that_redelivers_a_batch_does_not_duplicate_offers_or_events()
    {
        var hotel = TestData.Hotel(A, "a1", canonicalId: null, offers: ("Room X", 100m));
        var flaky = new StubSupplier(A, (call, _, _) => call == 1
            ? Script(Result.Ok<IReadOnlyList<Hotel>>([hotel]), Result.Fail<IReadOnlyList<Hotel>>(SupplierError.Transient("blip")))
            : Script(Result.Ok<IReadOnlyList<Hotel>>([hotel])));
        var resilience = OrchestratorHarness.FastResilience();
        resilience.Defaults.MaxAttempts = 2;
        var harness = new OrchestratorHarness([flaky], resilience: resilience);

        var updates = await harness.RunAsync();

        flaky.Calls.ShouldBe(2);
        updates.OfType<HotelsAdded>().ShouldHaveSingleItem();
        updates.OfType<HotelUpdated>().ShouldBeEmpty();
        Single<SupplierCompleted>(updates).HotelCount.ShouldBe(1);
        Single<Completed>(updates).Summary.OfferCount.ShouldBe(1);
    }

    [Fact]
    public async Task MaxResults_trims_the_collected_result_to_the_cheapest_hotels()
    {
        var harness = new OrchestratorHarness([StubSupplier.Returning(
            A,
            TestData.Hotel(A, "a1", "Pricey", offers: ("R", 300m)),
            TestData.Hotel(A, "a2", "Cheap", offers: ("R", 50m)),
            TestData.Hotel(A, "a3", "Middle", offers: ("R", 120m)))]);

        var result = await harness.Orchestrator.SearchToCompletionAsync(TestData.Request(maxResults: 2), TestData.Owner);

        result.Hotels.Select(static h => h.Name).ShouldBe(["Cheap", "Middle"]);
        result.Summary.HotelCount.ShouldBe(3, "the summary still describes the whole search");
    }

    [Fact]
    public async Task Concurrent_searches_by_different_owners_are_tracked_separately()
    {
        var harness = new OrchestratorHarness([StubSupplier.Returning(A, TestData.Hotel(A, "a1"))]);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            harness.Orchestrator.SearchToCompletionAsync(TestData.Request(), new SearchOwner(TestData.Owner.Tenant, $"client-{i}"))));

        results.Select(static r => r.SearchId).Distinct().Count().ShouldBe(8);
        results.ShouldAllBe(static r => r.Hotels.Count == 1);
        harness.Runs.ActiveCount.ShouldBe(0);
    }

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

    private static async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> Script(params Result<IReadOnlyList<Hotel>>[] items)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> ManyBatches([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < 1000; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return Result.Ok<IReadOnlyList<Hotel>>([TestData.Hotel(A, $"a{i}")]);
        }
    }

    private static async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> WaitForBothCore(
        StubSupplier self, Hotel hotel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (self.Calls < 2)
        {
            await Task.Delay(5, cancellationToken);
        }

        yield return Result.Ok<IReadOnlyList<Hotel>>([hotel]);
    }
}
