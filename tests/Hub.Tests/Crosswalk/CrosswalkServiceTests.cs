using Hub.Abstractions;
using Hub.Core;
using Hub.Mapping;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class CrosswalkServiceTests
{
    private static readonly SupplierId A = FakeSupplierIds.SupplierA;
    private static readonly SupplierId B = FakeSupplierIds.SupplierB;
    private static readonly SupplierId C = FakeSupplierIds.SupplierC;

    private static async Task<IReadOnlyDictionary<string, string>> CodesAsync(BookingHarness h, SupplierId supplier) =>
        (await h.Crosswalk.GetEntriesAsync(supplier.Value, default)).ToDictionary(static e => e.CanonicalCityId, static e => e.SupplierCityCode);

    [Fact]
    public async Task The_start_up_run_maps_the_demo_cities_for_all_three_suppliers_with_their_own_codes()
    {
        await using var h = await BookingHarness.CreateAsync();

        var a = await CodesAsync(h, A);
        var b = await CodesAsync(h, B);
        var c = await CodesAsync(h, C);

        a["city-aldermoor"].ShouldBe("ALD");
        a["city-brightwater"].ShouldBe("BRW");
        a["city-cedarfall"].ShouldBe("CDF");
        b["city-aldermoor"].ShouldBe("5001");
        b["city-brightwater"].ShouldBe("5002");
        b["city-cedarfall"].ShouldBe("5003", "\"Cedar Fall\" is a spelling variant of Cedarfall");
        c["city-aldermoor"].ShouldBe("ALDERMOOR");
        c["city-brightwater"].ShouldBe("BRIGHTWATER");
        c["city-cedarfall"].ShouldBe("CEDARFALL");
        b["city-saint-orrin"].ShouldBe("5004", "\"St. Orrin\" is an abbreviation of Saint Orrin");
        c["city-saint-orrin"].ShouldBe("SAINT-ORRIN");
        a["city-aldermoor-xb"].ShouldBe("ALX", "the same name in another country maps to that country's city");
        b["city-aldermoor-xb"].ShouldBe("5006");
        c["city-aldermoor-xb"].ShouldBe("ALDERMOOR-XB");
        a["city-brightwater-bay"].ShouldBe("BWB");
    }

    [Fact]
    public async Task Cities_that_are_not_clearly_canonical_wait_for_a_person_each_with_the_reason()
    {
        await using var h = await BookingHarness.CreateAsync();

        var pending = await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, null, default);

        pending.Select(static r => (r.Supplier, r.SupplierCityCode)).ShouldBe(
            [("supplier-b", "5007"), ("supplier-c", "ALDERMOOR-ZZ"), ("supplier-c", "NOWHEREVILLE")], ignoreOrder: true);
        pending.Single(static r => r.SupplierCityCode == "ALDERMOOR-ZZ").ProposedCityId.ShouldBeNull();
        pending.Single(static r => r.SupplierCityCode == "ALDERMOOR-ZZ").Reason.ShouldContain("alias table");
        var typo = pending.Single(static r => r.SupplierCityCode == "5007");
        typo.ProposedCityId.ShouldBe("city-brightwater");
        typo.Reason.ShouldContain("one code per supplier per city");
        var nowhere = pending.Single(static r => r.SupplierCityCode == "NOWHEREVILLE");
        nowhere.Reason.ShouldContain("below");
        nowhere.CandidatesJson.ShouldContain("city-");
    }

    [Fact]
    public async Task A_supplier_never_gets_two_codes_for_one_canonical_city_the_better_match_wins_and_the_other_is_queued()
    {
        await using var h = await BookingHarness.CreateAsync();

        var b = await CodesAsync(h, B);

        b["city-brightwater"].ShouldBe("5002", "the exact name beats the one-letter typo \"Brightwatr\"");
        b.Values.ShouldNotContain("5007");
        b.Values.Distinct().Count().ShouldBe(b.Count);
        (await h.Crosswalk.GetEntriesAsync(null, default)).GroupBy(static e => (e.Supplier, e.CanonicalCityId)).ShouldAllBe(static g => g.Count() == 1);
    }

    [Fact]
    public async Task Running_again_changes_nothing()
    {
        await using var h = await BookingHarness.CreateAsync();
        var entries = (await h.Crosswalk.GetEntriesAsync(null, default)).Count;
        var reviews = (await h.Crosswalk.GetReviewsAsync(null, null, default)).Count;

        var again = await h.Crosswalk.RunAsync(default);

        again.Accepted.ShouldBe(0);
        again.Reviewed.ShouldBe(0);
        again.Suppliers.ShouldAllBe(static s => s.Succeeded && s.Accepted == 0 && s.Reviewed == 0);
        again.Suppliers.Sum(static s => s.AlreadyMapped).ShouldBe(entries);
        (await h.Crosswalk.GetEntriesAsync(null, default)).Count.ShouldBe(entries);
        (await h.Crosswalk.GetReviewsAsync(null, null, default)).Count.ShouldBe(reviews);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_code_for_a_city_and_a_second_city_for_a_code()
    {
        await using var h = await BookingHarness.CreateAsync();
        var store = h.Provider.GetRequiredService<ICrosswalkStore>();
        CrosswalkEntry Entry(string code, string city) => new() { Supplier = "supplier-a", SupplierCityCode = code, SupplierCountryCode = "XA", CanonicalCityId = city, Source = "test" };

        (await store.TryAddEntryAsync(Entry("OTHER-CODE", "city-aldermoor"), default)).ShouldBe(AddEntryOutcome.CityTaken, "supplier-a already has ALD for Aldermoor");
        (await store.TryAddEntryAsync(Entry("ALD", "city-cedar-hollow"), default)).ShouldBe(AddEntryOutcome.CodeTaken, "ALD already means Aldermoor");
        (await store.TryAddEntryAsync(Entry("NEW-CODE", "city-cedar-hollow"), default)).ShouldBe(AddEntryOutcome.Added);
    }

    [Fact]
    public async Task Concurrent_runs_never_break_the_one_code_per_city_rule()
    {
        await using var h = await BookingHarness.CreateAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => await h.Crosswalk.RunAsync(default))));

        var entries = await h.Crosswalk.GetEntriesAsync(null, default);
        entries.GroupBy(static e => (e.Supplier, e.CanonicalCityId)).ShouldAllBe(static g => g.Count() == 1);
        entries.GroupBy(static e => (e.Supplier, e.SupplierCityCode)).ShouldAllBe(static g => g.Count() == 1);
    }

    // ---- decisions and what they do to a search ----

    private static async Task<SupplierOutcome> SearchOutcomeAsync(BookingHarness h, SupplierId supplier, string destination)
    {
        var result = await h.Orchestrator.SearchToCompletionAsync(
            BookingHarness.Request("EUR", supplier) with { Destination = destination }, new SearchOwner(BookingHarness.Tenant, "client"));
        return result.Summary.Suppliers.Single();
    }

    [Fact]
    public async Task A_supplier_without_a_mapped_code_for_the_destination_is_skipped_with_a_clear_outcome()
    {
        await using var h = await BookingHarness.CreateAsync();

        var outcome = await SearchOutcomeAsync(h, C, "city-aldermoor-heights");

        outcome.Status.ShouldBe(SupplierOutcomeStatus.Failed);
        outcome.Error!.Kind.ShouldBe(SupplierErrorKind.Unsupported);
        outcome.Error.Message.ShouldContain("not mapped");
    }

    [Fact]
    public async Task Search_resolves_supplier_codes_through_the_crosswalk()
    {
        await using var h = await BookingHarness.CreateAsync();

        foreach (var supplier in new[] { A, B, C })
        {
            var outcome = await SearchOutcomeAsync(h, supplier, "city-brightwater");

            outcome.Status.ShouldBe(SupplierOutcomeStatus.Succeeded, supplier.Value);
            outcome.HotelCount.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Approving_a_review_maps_the_city_and_the_next_search_uses_the_code()
    {
        await using var h = await BookingHarness.CreateAsync();
        var review = (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, "supplier-c", default)).Single(static r => r.SupplierCityCode == "NOWHEREVILLE");

        var result = await h.Crosswalk.ApproveAsync(review.Id, "city-aldermoor-heights", default);
        var outcome = await SearchOutcomeAsync(h, C, "city-aldermoor-heights");

        result.Resolution.ShouldBe(ReviewResolution.Done);
        result.Review!.Status.ShouldBe(ReviewStatus.Approved);
        result.Review.ResolvedCityId.ShouldBe("city-aldermoor-heights");
        (await CodesAsync(h, C))["city-aldermoor-heights"].ShouldBe("NOWHEREVILLE");
        outcome.Status.ShouldBe(SupplierOutcomeStatus.Succeeded, "the supplier is now asked, whatever it answers");
        (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, "supplier-c", default)).ShouldNotContain(r => r.Id == review.Id);
    }

    [Fact]
    public async Task Approving_without_naming_a_city_uses_the_proposal_and_conflicts_are_refused()
    {
        await using var h = await BookingHarness.CreateAsync();
        var typo = (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, "supplier-b", default)).Single();

        var conflict = await h.Crosswalk.ApproveAsync(typo.Id, null, default);

        conflict.Resolution.ShouldBe(ReviewResolution.Conflict, "supplier-b already has 5002 for Brightwater");
        conflict.Detail!.ShouldContain("already has a different code");
        (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, "supplier-b", default)).ShouldHaveSingleItem("a refused approval leaves the review pending");
        var elsewhere = await h.Crosswalk.ApproveAsync(typo.Id, "city-brightwater-bay", default);
        elsewhere.Resolution.ShouldBe(ReviewResolution.Conflict, "supplier-b has 5005 for Brightwater Bay already");
    }

    [Fact]
    public async Task Approving_an_unknown_city_or_a_review_without_a_proposal_is_refused()
    {
        await using var h = await BookingHarness.CreateAsync();
        var noProposal = (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, "supplier-c", default)).Single(static r => r.SupplierCityCode == "ALDERMOOR-ZZ");

        (await h.Crosswalk.ApproveAsync(noProposal.Id, null, default)).Resolution.ShouldBe(ReviewResolution.UnknownCity);
        (await h.Crosswalk.ApproveAsync(noProposal.Id, "city-nowhere", default)).Resolution.ShouldBe(ReviewResolution.UnknownCity);
        (await h.Crosswalk.ApproveAsync(9999, null, default)).Resolution.ShouldBe(ReviewResolution.NotFound);
    }

    [Fact]
    public async Task A_decided_review_cannot_be_decided_again_and_a_rejected_city_stays_out_after_another_run()
    {
        await using var h = await BookingHarness.CreateAsync();
        var review = (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, "supplier-c", default)).Single(static r => r.SupplierCityCode == "ALDERMOOR-ZZ");

        var rejected = await h.Crosswalk.RejectAsync(review.Id, default);
        var again = await h.Crosswalk.RejectAsync(review.Id, default);
        var approve = await h.Crosswalk.ApproveAsync(review.Id, "city-aldermoor", default);
        await h.Crosswalk.RunAsync(default);

        rejected.Resolution.ShouldBe(ReviewResolution.Done);
        again.Resolution.ShouldBe(ReviewResolution.NotPending);
        approve.Resolution.ShouldBe(ReviewResolution.NotPending);
        (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Pending, "supplier-c", default)).ShouldNotContain(r => r.SupplierCityCode == "ALDERMOOR-ZZ");
        (await h.Crosswalk.GetReviewsAsync(ReviewStatus.Rejected, null, default)).ShouldHaveSingleItem().SupplierCityCode.ShouldBe("ALDERMOOR-ZZ");
        (await CodesAsync(h, C)).Values.ShouldNotContain("ALDERMOOR-ZZ");
    }

    [Fact]
    public async Task A_supplier_whose_directory_cannot_be_read_is_reported_and_does_not_stop_the_others()
    {
        await using var h = await BookingHarness.CreateAsync();
        var registrations = new List<CityDirectoryRegistration>
        {
            new(SupplierId.Parse("supplier-x"), new BrokenDirectory()),
            h.Provider.GetServices<CityDirectoryRegistration>().First(static r => r.Supplier == FakeSupplierIds.SupplierA),
        };
        var service = new CrosswalkService(
            h.Provider.GetRequiredService<ICrosswalkStore>(), h.Provider.GetRequiredService<CrosswalkMatcher>(), registrations, h.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CrosswalkService>.Instance);

        var summary = await service.RunAsync(default);

        summary.Suppliers.Single(static s => s.Supplier == "supplier-x").Succeeded.ShouldBeFalse();
        summary.Suppliers.Single(static s => s.Supplier == "supplier-a").Succeeded.ShouldBeTrue();
    }

    private sealed class BrokenDirectory : ICityDirectory
    {
        public ValueTask<Result<SupplierDirectory>> GetDirectoryAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Fail<SupplierDirectory>(SupplierError.Unavailable("The directory is down.")));
    }
}
