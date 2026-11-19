using Hub.Abstractions;
using Hub.Booking;
using Hub.Core;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class BookingProcessorTests
{
    private static readonly SupplierId A = FakeSupplierIds.SupplierA;
    private static readonly SupplierId B = FakeSupplierIds.SupplierB;
    private static readonly SupplierId C = FakeSupplierIds.SupplierC;

    [Theory]
    [InlineData("supplier-a")]
    [InlineData("supplier-c")]
    public async Task A_synchronous_booking_is_confirmed_and_charges_exactly_the_amount_that_was_shown(string supplier)
    {
        await using var h = await BookingHarness.CreateAsync();
        var id = SupplierId.Parse(supplier);
        var offer = await h.FindOfferAsync(id);
        var quote = await h.QuoteAsync(offer);

        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "key-1"), default);

        outcome.Status.ShouldBe(BookingCreateStatus.Created);
        var booking = outcome.Booking!;
        booking.Status.ShouldBe(HubBookingStatus.Confirmed);
        booking.Selling.ShouldBe(quote.SellingPrice);
        booking.SupplierReference.ShouldStartWith("hub-");
        booking.SupplierBookingRef.ShouldNotBeNullOrWhiteSpace();
        var ledger = h.Ledgers.Bookings(id).ShouldHaveSingleItem();
        ledger.Total.ShouldBe(offer.Offer.Price.Amount, "the supplier was asked for the quoted supplier price");
        ledger.ClientRef.ShouldBe(booking.SupplierReference);
        (await h.Bookings.GetAsync(BookingHarness.Tenant, booking.Id, default))!.Status.ShouldBe(HubBookingStatus.Confirmed);
        (await h.QuoteStore.GetAsync(BookingHarness.Tenant, quote.QuoteId, default))!.Status.ShouldBe(QuoteStatus.Used);
    }

    [Fact]
    public async Task Repeating_a_request_returns_the_stored_outcome_and_never_books_twice()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        var first = await h.Bookings.CreateAsync(h.Command(quote, "same-key"), default);

        var again = await h.Bookings.CreateAsync(h.Command(quote, "same-key"), default);

        first.Status.ShouldBe(BookingCreateStatus.Created);
        again.Status.ShouldBe(BookingCreateStatus.Replayed);
        again.Booking!.Id.ShouldBe(first.Booking!.Id);
        h.Ledgers.Count(A).ShouldBe(1, "a duplicate submit is one supplier booking");
    }

    [Fact]
    public async Task The_same_key_with_a_different_payload_is_refused()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        await h.Bookings.CreateAsync(h.Command(quote, "reused"), default);

        var other = await h.Bookings.CreateAsync(h.Command(quote, "reused", guest: "Someone Else"), default);

        other.Status.ShouldBe(BookingCreateStatus.KeyReused);
        h.Ledgers.Count(A).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_submits_with_one_key_have_one_winner_and_one_supplier_booking()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await h.Bookings.CreateAsync(h.Command(quote, "race-key"), default))));

        outcomes.Count(static o => o.Status == BookingCreateStatus.Created).ShouldBe(1);
        outcomes.Count(static o => o.Status == BookingCreateStatus.Replayed).ShouldBe(7);
        outcomes.Select(static o => o.Booking!.Id).Distinct().ShouldHaveSingleItem();
        h.Ledgers.Count(A).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_submits_with_different_keys_for_one_quote_book_it_once()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            await h.Bookings.CreateAsync(h.Command(quote, $"key-{i}"), default))));

        outcomes.Count(static o => o.Status == BookingCreateStatus.Created).ShouldBe(1, "a quote can be used once");
        outcomes.Count(static o => o.Status == BookingCreateStatus.QuoteExpired).ShouldBe(5);
        h.Ledgers.Count(A).ShouldBe(1);
    }

    [Fact]
    public async Task A_quote_the_tenant_never_got_is_not_found()
    {
        await using var h = await BookingHarness.CreateAsync();

        var outcome = await h.Bookings.CreateAsync(new CreateBookingCommand(BookingHarness.Tenant, "k", "no-such-quote", 10m, "EUR", "Guest", PaymentMode.PayNow), default);

        outcome.Status.ShouldBe(BookingCreateStatus.QuoteNotFound);
    }

    [Fact]
    public async Task An_amount_other_than_the_one_shown_is_refused_with_the_current_quote()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));

        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "wrong-amount") with { Amount = quote.SellingPrice.Amount - 0.01m }, default);
        var wrongCurrency = await h.Bookings.CreateAsync(h.Command(quote, "wrong-currency") with { Currency = "USD" }, default);

        outcome.Status.ShouldBe(BookingCreateStatus.PriceChanged);
        outcome.CurrentQuote!.QuoteId.ShouldBe(quote.QuoteId);
        outcome.CurrentQuote.SellingPrice.ShouldBe(quote.SellingPrice);
        wrongCurrency.Status.ShouldBe(BookingCreateStatus.PriceChanged);
        h.Ledgers.Count(A).ShouldBe(0);
        (await h.QuoteStore.GetAsync(BookingHarness.Tenant, quote.QuoteId, default))!.Status.ShouldBe(QuoteStatus.Active, "a refused booking does not use up the quote");
    }

    [Fact]
    public async Task An_expired_quote_is_refused_and_a_fresh_one_is_offered()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(B);
        var quote = await h.QuoteAsync(offer);
        h.Time.Advance(TimeSpan.FromMinutes(29));
        h.Offers.Save(BookingHarness.Tenant, null, "hotel", offer.Offer, offer.Search);
        h.Time.Advance(TimeSpan.FromMinutes(2));

        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "too-late"), default);

        outcome.Status.ShouldBe(BookingCreateStatus.QuoteExpired);
        outcome.CurrentQuote.ShouldNotBeNull();
        outcome.CurrentQuote.QuoteId.ShouldNotBe(quote.QuoteId);
        h.Ledgers.Count(B).ShouldBe(0);
    }

    [Fact]
    public async Task A_superseded_quote_is_a_price_change_that_carries_the_new_quote()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        var first = await h.QuoteAsync(offer);
        h.Faults.Get(A)!.Set(new FaultProfile { PriceDriftPercent = 10 });
        var second = await h.QuoteAsync(offer);

        var outcome = await h.Bookings.CreateAsync(h.Command(first, "old-quote"), default);

        outcome.Status.ShouldBe(BookingCreateStatus.PriceChanged);
        outcome.CurrentQuote!.SellingPrice.ShouldBe(second.SellingPrice);
        h.Ledgers.Count(A).ShouldBe(0);
    }

    [Fact]
    public async Task A_price_that_moves_between_quote_and_booking_fails_the_booking_without_charging_anything()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        h.Faults.Get(A)!.Set(new FaultProfile { PriceDriftPercent = 10 });

        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "drifted"), default);

        outcome.Status.ShouldBe(BookingCreateStatus.Created);
        outcome.Booking!.Status.ShouldBe(HubBookingStatus.Failed);
        outcome.Booking.FailureReason.ShouldStartWith("PriceChanged");
        h.Ledgers.Count(A).ShouldBe(0);
    }

    // ---- Unknown outcomes ----

    [Fact]
    public async Task A_timeout_after_the_supplier_accepted_is_unknown_and_reconciliation_confirms_it_without_a_second_booking()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        h.Faults.Get(A)!.Set(new FaultProfile { AcceptedButLostRate = 1 });

        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "lost-answer"), default);
        h.Faults.Get(A)!.Set(FaultProfile.None);

        outcome.Booking!.Status.ShouldBe(HubBookingStatus.Unknown);
        h.Ledgers.Count(A).ShouldBe(1, "the supplier did book it");
        var settled = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking.Id, default);

        settled!.Status.ShouldBe(HubBookingStatus.Confirmed);
        settled.BookAttempts.ShouldBe(1, "the supplier was asked to book exactly once");
        settled.SupplierBookingRef.ShouldBe(h.Ledgers.Bookings(A).Single().BookingRef);
        h.Ledgers.Count(A).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_booking_the_supplier_never_recorded_is_booked_again_with_the_same_reference_after_it_is_proven_absent()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        h.Faults.Get(A)!.Set(new FaultProfile { ErrorRate = 1, ErrorKind = SupplierErrorKind.Timeout });
        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "never-arrived"), default);
        h.Faults.Get(A)!.Set(FaultProfile.None);

        outcome.Booking!.Status.ShouldBe(HubBookingStatus.Unknown);
        h.Ledgers.Count(A).ShouldBe(0);
        var settled = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking.Id, default);

        settled!.Status.ShouldBe(HubBookingStatus.Confirmed);
        settled.BookAttempts.ShouldBe(2);
        h.Ledgers.Bookings(A).ShouldHaveSingleItem().ClientRef.ShouldBe(outcome.Booking.SupplierReference);
    }

    [Fact]
    public async Task The_number_of_book_attempts_is_bounded()
    {
        await using var h = await BookingHarness.CreateAsync(("Hub:Bookings:MaxBookAttempts", "1"));
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        h.Faults.Get(A)!.Set(new FaultProfile { ErrorRate = 1, ErrorKind = SupplierErrorKind.Timeout });
        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "bounded"), default);
        h.Faults.Get(A)!.Set(FaultProfile.None);

        var settled = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking!.Id, default);

        settled!.Status.ShouldBe(HubBookingStatus.Failed);
        settled.FailureReason!.ShouldContain("no booking");
        h.Ledgers.Count(A).ShouldBe(0);
    }

    [Fact]
    public async Task A_supplier_that_cannot_look_bookings_up_leaves_an_unknown_booking_unknown_and_never_books_it_again()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(C));
        h.Faults.Get(C)!.Set(new FaultProfile { AcceptedButLostRate = 1 });
        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "c-lost"), default);
        h.Faults.Get(C)!.Set(FaultProfile.None);

        BookingRecord? settled = null;
        for (var i = 0; i < 3; i++)
        {
            settled = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking!.Id, default);
        }

        outcome.Booking!.Status.ShouldBe(HubBookingStatus.Unknown);
        settled!.Status.ShouldBe(HubBookingStatus.Unknown);
        settled.Note!.ShouldContain("manual");
        settled.BookAttempts.ShouldBe(1);
        h.Ledgers.Count(C).ShouldBe(1);
    }

    [Fact]
    public async Task A_malformed_booking_answer_is_an_unknown_outcome_not_a_failure()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        h.Faults.Get(A)!.Set(new FaultProfile { MalformedRate = 1 });

        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "garbled"), default);
        h.Faults.Get(A)!.Set(FaultProfile.None);
        var settled = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking!.Id, default);

        outcome.Booking.Status.ShouldBe(HubBookingStatus.Unknown);
        settled!.Status.ShouldBe(HubBookingStatus.Confirmed);
        h.Ledgers.Count(A).ShouldBe(1);
    }

    [Fact]
    public async Task A_pending_booking_that_never_recorded_an_answer_becomes_unknown_and_is_then_settled()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        var record = await h.QuoteStore.GetAsync(BookingHarness.Tenant, quote.QuoteId, default);
        var orphan = new BookingRecord
        {
            Id = "orphan",
            Tenant = BookingHarness.Tenant,
            IdempotencyKey = "orphan-key",
            PayloadHash = "x",
            QuoteId = quote.QuoteId,
            OfferId = quote.OfferId,
            Supplier = "supplier-a",
            SupplierHotelCode = "x",
            SupplierReference = BookingProcessor.ReferenceFor(BookingHarness.Tenant, "orphan-key"),
            Status = HubBookingStatus.Pending,
            SellingAmount = record!.SellingAmount,
            SellingCurrency = record.SellingCurrency,
            SupplierAmount = record.SupplierAmount,
            SupplierCurrency = record.SupplierCurrency,
            LeadGuestName = "Guest",
            CreatedAt = h.Time.GetUtcNow(),
            UpdatedAt = h.Time.GetUtcNow(),
            OfferJson = record.OfferJson,
            SearchJson = record.SearchJson,
        };
        (await h.BookingStore.ClaimAsync(orphan, h.Time.GetUtcNow(), default)).ShouldBe(ClaimOutcome.Claimed);

        (await h.Bookings.SyncAsync(BookingHarness.Tenant, "orphan", default))!.Status.ShouldBe(HubBookingStatus.Pending, "it may still be in flight");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        (await h.Bookings.SyncAsync(BookingHarness.Tenant, "orphan", default))!.Status.ShouldBe(HubBookingStatus.Unknown);
        var settled = await h.Bookings.SyncAsync(BookingHarness.Tenant, "orphan", default);

        settled!.Status.ShouldBe(HubBookingStatus.Confirmed);
        h.Ledgers.Count(A).ShouldBe(1);
    }

    [Fact]
    public async Task The_background_sweep_settles_unknown_bookings_that_are_old_enough()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        h.Faults.Get(A)!.Set(new FaultProfile { AcceptedButLostRate = 1 });
        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "sweep-me"), default);
        h.Faults.Get(A)!.Set(FaultProfile.None);

        (await h.Bookings.SweepAsync(default)).ShouldBeEmpty("a booking that was just changed is left alone");
        h.Time.Advance(TimeSpan.FromSeconds(10));
        var swept = await h.Bookings.SweepAsync(default);

        swept.ShouldHaveSingleItem().Status.ShouldBe(HubBookingStatus.Confirmed);
        (await h.Bookings.GetAsync(BookingHarness.Tenant, outcome.Booking!.Id, default))!.Status.ShouldBe(HubBookingStatus.Confirmed);
    }

    // ---- Asynchronous confirmation ----

    [Fact]
    public async Task An_asynchronous_supplier_accepts_first_and_the_booking_is_confirmed_by_polling()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(B));

        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "async-1"), default);
        var early = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking!.Id, default);
        h.Time.Advance(TimeSpan.FromSeconds(6));
        var later = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking.Id, default);

        outcome.Booking.Status.ShouldBe(HubBookingStatus.Pending, "the supplier only accepted it");
        early!.Status.ShouldBe(HubBookingStatus.Pending);
        early.SupplierBookingRef.ShouldNotBeNullOrWhiteSpace();
        later!.Status.ShouldBe(HubBookingStatus.Confirmed);
    }

    [Fact]
    public async Task An_asynchronous_booking_the_supplier_later_fails_ends_failed()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(B));
        h.Faults.Get(B)!.Set(new FaultProfile { BookingFailRate = 1 });
        var outcome = await h.Bookings.CreateAsync(h.Command(quote, "async-fail"), default);
        h.Faults.Get(B)!.Set(FaultProfile.None);

        h.Time.Advance(TimeSpan.FromSeconds(6));
        var settled = await h.Bookings.SyncAsync(BookingHarness.Tenant, outcome.Booking!.Id, default);

        settled!.Status.ShouldBe(HubBookingStatus.Failed);
    }

    // ---- Pay later ----

    private static async Task<QuoteView> PayLaterQuoteAsync(BookingHarness h, StoredOffer offer, DateTimeOffset? freeUntil)
    {
        h.Offers.Save(BookingHarness.Tenant, offer.CanonicalId, offer.HotelName, offer.Offer with { Refundable = freeUntil is not null, RefundableUntil = freeUntil, PolicyKnown = true }, offer.Search);
        return await h.QuoteAsync(h.Offers.Get(BookingHarness.Tenant, offer.Offer.OfferId)!);
    }

    [Fact]
    public async Task Pay_later_is_allowed_with_a_far_deadline_and_the_pay_by_moment_is_stored()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        var now = h.Time.GetUtcNow();
        var far = await PayLaterQuoteAsync(h, offer, now + TimeSpan.FromDays(10));

        var allowed = await h.Bookings.CreateAsync(h.Command(far, "pl-far", mode: PaymentMode.PayLater), default);

        allowed.Status.ShouldBe(BookingCreateStatus.Created);
        allowed.Booking!.PaymentMode.ShouldBe(PaymentMode.PayLater);
        allowed.Booking.FreeCancellationUntil.ShouldBe(now + TimeSpan.FromDays(10));
        allowed.Booking.PayBy.ShouldBe(now + TimeSpan.FromDays(9), "one day before the free window closes");
    }

    [Fact]
    public async Task Pay_later_is_refused_for_a_deadline_inside_the_lead_time_or_without_a_policy()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        var now = h.Time.GetUtcNow();

        var near = await PayLaterQuoteAsync(h, offer, now + TimeSpan.FromDays(2));
        var nearOutcome = await h.Bookings.CreateAsync(h.Command(near, "pl-near", mode: PaymentMode.PayLater), default);
        var none = await PayLaterQuoteAsync(h, offer, null);
        var noneOutcome = await h.Bookings.CreateAsync(h.Command(none, "pl-none", mode: PaymentMode.PayLater), default);

        nearOutcome.Status.ShouldBe(BookingCreateStatus.PayLaterNotAllowed);
        noneOutcome.Status.ShouldBe(BookingCreateStatus.PayLaterNotAllowed);
        h.Ledgers.Count(A).ShouldBe(0);
    }

    [Fact]
    public async Task An_unpaid_pay_later_booking_is_cancelled_before_the_penalty_window_and_a_paid_one_is_left_alone()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        var until = h.Time.GetUtcNow() + TimeSpan.FromDays(10);
        var unpaidQuote = await PayLaterQuoteAsync(h, offer, until);
        var unpaid = (await h.Bookings.CreateAsync(h.Command(unpaidQuote, "unpaid", mode: PaymentMode.PayLater), default)).Booking!;
        var paidQuote = await PayLaterQuoteAsync(h, offer, until);
        var paid = (await h.Bookings.CreateAsync(h.Command(paidQuote, "paid", mode: PaymentMode.PayLater), default)).Booking!;
        (await h.Bookings.MarkPaidAsync(BookingHarness.Tenant, paid.Id, default)).Accepted.ShouldBeTrue();
        var service = new PayLaterExpiryService(h.Bookings, Options.Create(new BookingOptions()), h.Time, NullLogger<PayLaterExpiryService>.Instance);

        h.Time.Advance(TimeSpan.FromDays(8));
        (await service.RunOnceAsync(default)).ShouldBe(0, "the pay-by moment has not arrived");
        h.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        (await service.RunOnceAsync(default)).ShouldBe(1);
        (await service.RunOnceAsync(default)).ShouldBe(0, "nothing is cancelled twice");

        var afterUnpaid = await h.Bookings.GetAsync(BookingHarness.Tenant, unpaid.Id, default);
        var afterPaid = await h.Bookings.GetAsync(BookingHarness.Tenant, paid.Id, default);
        afterUnpaid!.Status.ShouldBe(HubBookingStatus.Cancelled);
        afterUnpaid.CancelStatus.ShouldBe(nameof(BookingCancelStatus.Cancelled), "cancelled inside the free window, so no penalty");
        afterUnpaid.PenaltyAmount.ShouldBeNull();
        afterUnpaid.Note!.ShouldContain("payment was not received");
        afterPaid!.Status.ShouldBe(HubBookingStatus.Confirmed);
        h.Ledgers.Bookings(A).Select(static b => b.State).ShouldBe(["Cancelled", "Confirmed"], ignoreOrder: true);
    }

    [Fact]
    public async Task The_expiry_service_tries_again_when_the_supplier_cannot_cancel_yet()
    {
        await using var h = await BookingHarness.CreateAsync();
        var offer = await h.FindOfferAsync(A);
        var quote = await PayLaterQuoteAsync(h, offer, h.Time.GetUtcNow() + TimeSpan.FromDays(10));
        var booking = (await h.Bookings.CreateAsync(h.Command(quote, "retry-expiry", mode: PaymentMode.PayLater), default)).Booking!;
        var service = new PayLaterExpiryService(h.Bookings, Options.Create(new BookingOptions()), h.Time, NullLogger<PayLaterExpiryService>.Instance);
        h.Time.Advance(TimeSpan.FromDays(9) + TimeSpan.FromMinutes(1));
        h.Faults.Get(A)!.Set(new FaultProfile { ErrorRate = 1, ErrorKind = SupplierErrorKind.Unavailable });

        (await service.RunOnceAsync(default)).ShouldBe(0);
        (await h.Bookings.GetAsync(BookingHarness.Tenant, booking.Id, default))!.Status.ShouldBe(HubBookingStatus.Confirmed);
        h.Faults.Get(A)!.Set(FaultProfile.None);
        (await service.RunOnceAsync(default)).ShouldBe(1);

        (await h.Bookings.GetAsync(BookingHarness.Tenant, booking.Id, default))!.Status.ShouldBe(HubBookingStatus.Cancelled);
    }

    [Fact]
    public async Task Marking_a_booking_paid_is_idempotent_and_refused_for_a_cancelled_booking()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        var booking = (await h.Bookings.CreateAsync(h.Command(quote, "pay-me"), default)).Booking!;

        var first = await h.Bookings.MarkPaidAsync(BookingHarness.Tenant, booking.Id, default);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        var second = await h.Bookings.MarkPaidAsync(BookingHarness.Tenant, booking.Id, default);
        await h.Bookings.CancelAsync(BookingHarness.Tenant, booking.Id, null, default);
        var afterCancel = await h.Bookings.MarkPaidAsync(BookingHarness.Tenant, booking.Id, default);

        first.Accepted.ShouldBeTrue();
        second.Booking!.PaidAt.ShouldBe(first.Booking!.PaidAt, "the first payment time stands");
        afterCancel.Accepted.ShouldBeFalse();
        (await h.Bookings.MarkPaidAsync(BookingHarness.Tenant, "missing", default)).Booking.ShouldBeNull();
    }

    // ---- Cancel ----

    [Theory]
    [InlineData("supplier-a")]
    [InlineData("supplier-b")]
    [InlineData("supplier-c")]
    public async Task Cancelling_inside_the_free_window_is_reported_as_cancelled_whatever_the_supplier(string supplier)
    {
        await using var h = await BookingHarness.CreateAsync();
        var id = SupplierId.Parse(supplier);
        var quote = await h.QuoteAsync(await h.FindOfferAsync(id));
        var booking = (await h.Bookings.CreateAsync(h.Command(quote, "cancel-free"), default)).Booking!;

        var result = await h.Bookings.CancelAsync(BookingHarness.Tenant, booking.Id, null, default);
        var again = await h.Bookings.CancelAsync(BookingHarness.Tenant, booking.Id, null, default);

        result.Outcome!.Status.ShouldBe(BookingCancelStatus.Cancelled);
        result.Outcome.Penalty.ShouldBeNull();
        result.Outcome.Refund.ShouldBeNull("nothing was paid");
        result.Booking!.Status.ShouldBe(HubBookingStatus.Cancelled);
        again.Outcome!.Status.ShouldBe(BookingCancelStatus.AlreadyCancelled);
    }

    [Theory]
    [InlineData("supplier-a")]
    [InlineData("supplier-b")]
    [InlineData("supplier-c")]
    public async Task Cancelling_late_reports_the_penalty_in_the_selling_currency_and_refunds_the_rest_of_what_was_paid(string supplier)
    {
        await using var h = await BookingHarness.CreateAsync();
        var id = SupplierId.Parse(supplier);
        var offer = await h.FindOfferAsync(id);
        var quote = await h.QuoteAsync(offer);
        var booking = (await h.Bookings.CreateAsync(h.Command(quote, "cancel-late"), default)).Booking!;
        await h.Bookings.MarkPaidAsync(BookingHarness.Tenant, booking.Id, default);
        h.Time.SetUtcNow(new DateTimeOffset(2027, 2, 28, 12, 0, 0, TimeSpan.Zero));

        var result = await h.Bookings.CancelAsync(BookingHarness.Tenant, booking.Id, null, default);

        var outcome = result.Outcome!;
        outcome.Status.ShouldBe(BookingCancelStatus.PenaltyApplied);
        outcome.Penalty!.Value.Currency.ShouldBe("EUR");
        outcome.Penalty.Value.Amount.ShouldBeGreaterThan(0);
        var supplierPenalty = offer.Offer.Price.Amount / 3m; // one night of a three-night stay
        outcome.Penalty.Value.Amount.ShouldBe(Math.Round(supplierPenalty * 1.10m, 2, MidpointRounding.AwayFromZero), 0.02m);
        (outcome.Penalty.Value.Amount + outcome.Refund!.Value.Amount).ShouldBe(quote.SellingPrice.Amount, "penalty plus refund is what was paid");
        result.Booking!.PenaltyAmount.ShouldBe(outcome.Penalty.Value.Amount);
    }

    [Fact]
    public async Task A_cancel_the_supplier_cannot_complete_is_reported_as_failed_and_leaves_the_booking_alone()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        var booking = (await h.Bookings.CreateAsync(h.Command(quote, "cancel-fails"), default)).Booking!;
        h.Faults.Get(A)!.Set(new FaultProfile { ErrorRate = 1, ErrorKind = SupplierErrorKind.Unavailable });

        var result = await h.Bookings.CancelAsync(BookingHarness.Tenant, booking.Id, null, default);

        result.Outcome!.Status.ShouldBe(BookingCancelStatus.Failed);
        result.Outcome.Detail.ShouldNotBeNullOrWhiteSpace();
        (await h.Bookings.GetAsync(BookingHarness.Tenant, booking.Id, default))!.Status.ShouldBe(HubBookingStatus.Confirmed);
    }

    [Fact]
    public async Task Cancelling_an_unknown_booking_is_refused_until_it_is_settled_and_a_missing_one_is_not_found()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        h.Faults.Get(A)!.Set(new FaultProfile { AcceptedButLostRate = 1 });
        var booking = (await h.Bookings.CreateAsync(h.Command(quote, "cancel-unknown"), default)).Booking!;
        h.Faults.Get(A)!.Set(FaultProfile.None);

        var refused = await h.Bookings.CancelAsync(BookingHarness.Tenant, booking.Id, null, default);
        var missing = await h.Bookings.CancelAsync(BookingHarness.Tenant, "missing", null, default);

        refused.Refusal.ShouldBe(BookingCancelRefusal.NotCancellable);
        missing.Refusal.ShouldBe(BookingCancelRefusal.NotFound);
    }

    [Fact]
    public async Task A_booking_is_visible_only_to_its_own_tenant()
    {
        await using var h = await BookingHarness.CreateAsync();
        var quote = await h.QuoteAsync(await h.FindOfferAsync(A));
        var booking = (await h.Bookings.CreateAsync(h.Command(quote, "mine"), default)).Booking!;

        (await h.Bookings.GetAsync("another-tenant", booking.Id, default)).ShouldBeNull();
        (await h.Bookings.SyncAsync("another-tenant", booking.Id, default)).ShouldBeNull();
        (await h.Bookings.CancelAsync("another-tenant", booking.Id, null, default)).Refusal.ShouldBe(BookingCancelRefusal.NotFound);
    }
}
