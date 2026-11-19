using Hub.Abstractions;
using Hub.Booking;
using Hub.Core;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class QuoteServiceTests
{
    private static readonly SupplierId A = FakeSupplierIds.SupplierA;
    private static readonly SupplierId B = FakeSupplierIds.SupplierB;
    private static readonly SupplierId C = FakeSupplierIds.SupplierC;

    private static decimal WithMarkup(decimal supplier) => Math.Round(supplier * 1.10m, 2, MidpointRounding.AwayFromZero);

    [Theory]
    [InlineData("supplier-a")]
    [InlineData("supplier-b")]
    [InlineData("supplier-c")]
    public async Task An_unchanged_price_gives_a_quote_with_the_selling_price_and_no_change(string supplier)
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(SupplierId.Parse(supplier));

        var quote = await h.QuoteAsync(offer);

        quote.SellingPrice.ShouldBe(offer.Offer.SellingPrice);
        quote.SellingPrice.Amount.ShouldBe(WithMarkup(offer.Offer.Price.Amount));
        quote.PriceChanged.ShouldBeNull();
        quote.OfferId.ShouldBe(offer.Offer.OfferId);
        quote.ExpiresAt.ShouldBeGreaterThan(h.Time.GetUtcNow());
        quote.ExpiresAt.ShouldBeLessThanOrEqualTo(h.Time.GetUtcNow() + TimeSpan.FromMinutes(30), "never longer than the configured lifetime");
        var stored = await h.QuoteStore.GetAsync(BookingHarness.Tenant, quote.QuoteId, default);
        stored!.Status.ShouldBe(QuoteStatus.Active);
        stored.SupplierAmount.ShouldBe(offer.Offer.Price.Amount);
    }

    [Theory]
    [InlineData("supplier-a")]
    [InlineData("supplier-b")]
    [InlineData("supplier-c")]
    public async Task A_price_that_moved_is_reported_and_written_back_to_the_stored_offer(string supplier)
    {
        await using var h = await BookingHarness.CreateAsync();
        var id = SupplierId.Parse(supplier);
        var offer = await h.FindOfferAsync(id);
        var before = offer.Offer.SellingPrice;
        h.Faults.Get(id)!.Set(new FaultProfile { PriceDriftPercent = 10 });

        var quote = await h.QuoteAsync(offer);

        var movedSupplierPrice = Math.Round(offer.Offer.Price.Amount * 1.10m, 2, MidpointRounding.AwayFromZero);
        quote.PriceChanged.ShouldNotBeNull();
        quote.PriceChanged.From.ShouldBe(before);
        quote.PriceChanged.To.ShouldBe(quote.SellingPrice);
        quote.SellingPrice.Amount.ShouldBe(WithMarkup(movedSupplierPrice));
        var later = h.Offers.Get(BookingHarness.Tenant, offer.Offer.OfferId)!;
        later.Offer.SellingPrice.ShouldBe(quote.SellingPrice, "later reads show the new price");
        later.Offer.Price.Amount.ShouldBe(movedSupplierPrice);
    }

    [Fact]
    public async Task Quoting_again_at_the_same_price_reports_no_change_and_supersedes_the_earlier_quote()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        var first = await h.QuoteAsync(offer);

        var second = await h.QuoteAsync(offer);

        second.QuoteId.ShouldNotBe(first.QuoteId);
        second.PriceChanged.ShouldBeNull();
        (await h.QuoteStore.GetAsync(BookingHarness.Tenant, first.QuoteId, default))!.Status.ShouldBe(QuoteStatus.Superseded);
        (await h.QuoteStore.GetAsync(BookingHarness.Tenant, second.QuoteId, default))!.Status.ShouldBe(QuoteStatus.Active);
    }

    [Fact]
    public async Task A_second_quote_after_a_drift_shows_no_further_change_because_the_new_price_was_written_back()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        h.Faults.Get(A)!.Set(new FaultProfile { PriceDriftPercent = 10 });
        var first = await h.QuoteAsync(offer);

        var second = await h.QuoteAsync(offer);

        first.PriceChanged.ShouldNotBeNull();
        second.PriceChanged.ShouldBeNull();
        second.SellingPrice.ShouldBe(first.SellingPrice);
    }

    [Fact]
    public async Task A_quote_never_outlives_the_price_hold_the_supplier_announced()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);

        var quote = await h.QuoteAsync(offer);

        quote.ExpiresAt.ShouldBe(h.Time.GetUtcNow() + TimeSpan.FromMinutes(15), "supplier-a holds a price for 15 minutes, less than the configured 30");
    }

    [Fact]
    public async Task An_expired_supplier_session_is_reported_as_such()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(C);
        h.Time.Advance(TimeSpan.FromSeconds(61));

        var outcome = await h.Quotes.QuoteAsync(BookingHarness.Tenant, offer.Offer.OfferId, default);

        outcome.Failure.ShouldBe(QuoteFailure.SessionExpired);
        outcome.Quote.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_offer_or_another_tenants_offer_is_not_found()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);

        (await h.Quotes.QuoteAsync(BookingHarness.Tenant, "no-such-offer", default)).Failure.ShouldBe(QuoteFailure.OfferNotFound);
        (await h.Quotes.QuoteAsync("another-tenant", offer.Offer.OfferId, default)).Failure.ShouldBe(QuoteFailure.OfferNotFound);
    }

    [Fact]
    public async Task An_offer_that_is_no_longer_kept_cannot_be_quoted()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        h.Time.Advance(TimeSpan.FromMinutes(31));

        (await h.Quotes.QuoteAsync(BookingHarness.Tenant, offer.Offer.OfferId, default)).Failure.ShouldBe(QuoteFailure.OfferNotFound);
    }

    [Fact]
    public async Task A_supplier_failure_is_reported_without_a_quote()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        h.Faults.Get(A)!.Set(new FaultProfile { ErrorRate = 1, ErrorKind = SupplierErrorKind.Unavailable });

        var outcome = await h.Quotes.QuoteAsync(BookingHarness.Tenant, offer.Offer.OfferId, default);

        outcome.Failure.ShouldBe(QuoteFailure.SupplierFailed);
    }

    [Fact]
    public async Task Offers_of_different_party_sizes_get_different_ids_so_they_never_overwrite_each_other()
    {
        await using var h = await BookingHarness.CreateAsync();
        var owner = new SearchOwner(BookingHarness.Tenant, "client");
        var two = await h.Orchestrator.SearchToCompletionAsync(BookingHarness.Request("EUR", A), owner);
        var one = await h.Orchestrator.SearchToCompletionAsync(
            BookingHarness.Request("EUR", A) with { Rooms = [new Occupancy(1)] }, owner);

        var twoIds = two.Hotels.SelectMany(static h => h.Offers).Select(static o => o.OfferId).ToHashSet();
        var oneIds = one.Hotels.SelectMany(static h => h.Offers).Select(static o => o.OfferId).ToHashSet();

        twoIds.Overlaps(oneIds).ShouldBeFalse();
        twoIds.ShouldAllBe(static id => id.Contains('~'));
    }
}

public class PricingServiceTests
{
    private static TenantMarkupPricing Pricing(decimal markup = 10, Dictionary<string, decimal>? perSupplier = null) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new TenantOptions
            {
                Tenants = new() { ["t"] = new() { MarkupPercent = markup, SupplierMarkupPercent = perSupplier ?? [] } },
            }),
            Microsoft.Extensions.Options.Options.Create(new PricingOptions { FxRates = new() { ["EUR/USD"] = 1.08m } }));

    [Fact]
    public void The_same_currency_only_adds_the_markup()
    {
        Pricing().ToSellingPrice("t", TestData.A, new Money(200m, "EUR"), "EUR").ShouldBe(new Money(220m, "EUR"));
    }

    [Fact]
    public void A_configured_rate_converts_before_the_markup_and_its_inverse_is_derived()
    {
        Pricing(0).ToSellingPrice("t", TestData.A, new Money(100m, "EUR"), "USD").ShouldBe(new Money(108m, "USD"));
        Pricing(0).ToSellingPrice("t", TestData.A, new Money(108m, "USD"), "EUR").ShouldBe(new Money(100m, "EUR"));
        Pricing(10).ToSellingPrice("t", TestData.A, new Money(100m, "EUR"), "USD").ShouldBe(new Money(118.80m, "USD"));
    }

    [Fact]
    public void A_currency_pair_without_a_rate_cannot_be_priced()
    {
        Pricing().ToSellingPrice("t", TestData.A, new Money(100m, "EUR"), "XYZ").ShouldBeNull();
    }

    [Fact]
    public void A_supplier_markup_replaces_the_tenant_markup_for_that_supplier_only()
    {
        var pricing = Pricing(10, new Dictionary<string, decimal> { ["supplier-b"] = 5 });

        pricing.ToSellingPrice("t", TestData.A, new Money(100m, "EUR"), "EUR").ShouldBe(new Money(110m, "EUR"));
        pricing.ToSellingPrice("t", TestData.B, new Money(100m, "EUR"), "EUR").ShouldBe(new Money(105m, "EUR"));
    }

    [Fact]
    public void An_unknown_tenant_pays_the_supplier_price()
    {
        Pricing().ToSellingPrice("other", TestData.A, new Money(100m, "EUR"), "EUR").ShouldBe(new Money(100m, "EUR"));
    }

    [Fact]
    public void Rates_that_are_not_positive_or_not_named_like_a_pair_stop_startup()
    {
        var validator = new PricingOptionsValidator();

        validator.Validate(null, new PricingOptions { FxRates = new() { ["EUR/USD"] = 0m } }).Failed.ShouldBeTrue();
        validator.Validate(null, new PricingOptions { FxRates = new() { ["EURUSD"] = 1.1m } }).Failed.ShouldBeTrue();
        validator.Validate(null, new PricingOptions { FxRates = new() { ["EUR/EUR"] = 1m } }).Failed.ShouldBeTrue();
        validator.Validate(null, new PricingOptions { FxRates = new() { ["EUR/USD"] = 1.08m } }).Succeeded.ShouldBeTrue();
    }
}
