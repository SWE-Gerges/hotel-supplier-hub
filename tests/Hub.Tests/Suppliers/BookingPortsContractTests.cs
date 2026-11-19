using Hub.Abstractions;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Hub.Tests;

/// <summary>
/// What every supplier that implements a booking capability must show, whatever its protocol. Capabilities a supplier
/// does not have are asserted as absent, so the hub can rely on the registry to know what to call.
/// </summary>
public abstract class BookingPortsContractTests : IDisposable
{
    protected BookingPortsContractTests()
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2027, 1, 10, 9, 0, 0, TimeSpan.Zero));
        Provider = FakeSuppliersHarness.Build(Time);
        Search = Provider.GetRequiredKeyedService<IHotelSearch>(Id);
        Faults = Provider.GetRequiredService<FakeSupplierFaults>().Get(Id)!;
        Ledgers = Provider.GetRequiredService<FakeSupplierLedgers>();
    }

    protected abstract SupplierId Id { get; }

    protected abstract SupplierCityCodes Codes { get; }

    /// <summary>Capabilities beyond search that this supplier offers.</summary>
    protected abstract SupplierCapabilities Expected { get; }

    private static readonly Hub.Core.CancellationPolicyNormalizer Policies = PolicyHarness.Create();

    protected FakeTimeProvider Time { get; }

    protected ServiceProvider Provider { get; }

    protected IHotelSearch Search { get; }

    protected FaultController Faults { get; }

    protected FakeSupplierLedgers Ledgers { get; }

    public void Dispose() => Provider.Dispose();

    protected T? Port<T>()
        where T : class => Provider.GetKeyedService<T>(Id);

    protected SupplierSearchQuery Query() => new(
        new HotelSearchRequest
        {
            Destination = "city-aldermoor",
            CheckIn = new DateOnly(2027, 3, 1),
            CheckOut = new DateOnly(2027, 3, 4),
            Rooms = [new Occupancy(2)],
            Nationality = "XA",
            Currency = "EUR",
        },
        Codes);

    /// <summary>The first refundable (or non-refundable) offer the supplier returns, with the search that produced it.</summary>
    protected async Task<OfferRef> FindOfferAsync(bool refundable = true)
    {
        var query = Query();
        await foreach (var batch in Search.SearchAsync(query, CancellationToken.None))
        {
            batch.IsSuccess.ShouldBeTrue();
            foreach (var hotel in batch.Value)
            {
                foreach (var offer in hotel.Offers.Select(o => Policies.Apply(o, query.Request.Stay, Time.GetUtcNow())).Where(o => o.Refundable == refundable))
                {
                    return new OfferRef(offer, query);
                }
            }
        }

        throw new InvalidOperationException("The supplier returned no matching offer.");
    }

    protected static BookRequest BookRequestFor(OfferRef offer, string reference, decimal? price = null) =>
        new(offer, new Money(price ?? offer.Offer.Price.Amount, offer.Offer.Price.Currency), reference, "Test Guest");

    protected static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        return result.Value;
    }

    [Fact]
    public void The_supplier_offers_exactly_the_capabilities_it_declares()
    {
        var descriptor = Provider.GetRequiredKeyedService<ISupplierDescriptor>(Id);

        descriptor.Capabilities.ShouldBe(SupplierCapabilities.Search | SupplierCapabilities.DuplicateDetection | SupplierCapabilities.CityDirectory | Expected);
        (Port<IRoomAvailability>() is not null).ShouldBe(Expected.HasFlag(SupplierCapabilities.RoomAvailability));
        (Port<IPreBook>() is not null).ShouldBe(Expected.HasFlag(SupplierCapabilities.PreBook));
        (Port<IBook>() is not null).ShouldBe(Expected.HasFlag(SupplierCapabilities.Book));
        (Port<ICancel>() is not null).ShouldBe(Expected.HasFlag(SupplierCapabilities.Cancel));
        (Port<IRetrieve>() is not null).ShouldBe(Expected.HasFlag(SupplierCapabilities.Retrieve));
    }

    [Fact]
    public async Task Booking_at_the_quoted_price_succeeds_and_the_supplier_records_it_once()
    {
        var offer = await FindOfferAsync();

        var confirmation = Ok(await Port<IBook>()!.BookAsync(BookRequestFor(offer, "ref-one"), CancellationToken.None));

        confirmation.SupplierBookingRef.ShouldNotBeNullOrWhiteSpace();
        confirmation.Status.ShouldBeOneOf(BookingStatus.Confirmed, BookingStatus.Pending);
        confirmation.Total.ShouldBe(offer.Offer.Price);
        Ledgers.Count(Id).ShouldBe(1);
    }

    [Fact]
    public async Task Booking_the_same_reference_twice_returns_the_same_booking_and_never_books_twice()
    {
        var offer = await FindOfferAsync();
        var book = Port<IBook>()!;

        var first = Ok(await book.BookAsync(BookRequestFor(offer, "dup-ref"), CancellationToken.None));
        var second = Ok(await book.BookAsync(BookRequestFor(offer, "dup-ref"), CancellationToken.None));

        second.SupplierBookingRef.ShouldBe(first.SupplierBookingRef);
        Ledgers.Count(Id).ShouldBe(1, "duplicate detection by supplier reference");
    }

    [Fact]
    public async Task A_price_that_moved_since_the_quote_is_refused_with_a_typed_error()
    {
        var offer = await FindOfferAsync();
        Faults.Set(new FaultProfile { PriceDriftPercent = 10 });

        var result = await Port<IBook>()!.BookAsync(BookRequestFor(offer, "drifted"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Kind.ShouldBe(SupplierErrorKind.PriceChanged);
        Ledgers.Count(Id).ShouldBe(0);
    }

    [Fact]
    public async Task A_booking_whose_answer_is_lost_is_still_recorded_and_a_retry_does_not_book_again()
    {
        var offer = await FindOfferAsync();
        var book = Port<IBook>()!;
        Faults.Set(new FaultProfile { AcceptedButLostRate = 1 });

        var lost = await book.BookAsync(BookRequestFor(offer, "lost-answer"), CancellationToken.None);

        lost.IsFailure.ShouldBeTrue();
        lost.Error.Kind.ShouldBe(SupplierErrorKind.Timeout);
        Ledgers.Count(Id).ShouldBe(1, "the supplier recorded the booking before its answer was lost");

        Faults.Set(FaultProfile.None);
        var retry = Ok(await book.BookAsync(BookRequestFor(offer, "lost-answer"), CancellationToken.None));
        retry.SupplierBookingRef.ShouldBe(Ledgers.Bookings(Id).Single().BookingRef);
        Ledgers.Count(Id).ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_inside_the_free_window_costs_nothing_and_cancelling_again_says_so()
    {
        var offer = await FindOfferAsync();
        var confirmation = Ok(await Port<IBook>()!.BookAsync(BookRequestFor(offer, "cancel-free"), CancellationToken.None));
        var cancel = Port<ICancel>()!;

        var first = Ok(await cancel.CancelAsync(new CancelRequest(confirmation.SupplierBookingRef), CancellationToken.None));
        var second = Ok(await cancel.CancelAsync(new CancelRequest(confirmation.SupplierBookingRef), CancellationToken.None));

        first.Status.ShouldBe(SupplierCancelStatus.Cancelled);
        first.Penalty.ShouldBeNull();
        first.Refund!.Value.Amount.ShouldBe(offer.Offer.Price.Amount);
        second.Status.ShouldBe(SupplierCancelStatus.AlreadyCancelled);
    }

    [Fact]
    public async Task Cancelling_after_the_free_window_reports_the_penalty_whatever_shape_the_supplier_answers_in()
    {
        var offer = await FindOfferAsync();
        var confirmation = Ok(await Port<IBook>()!.BookAsync(BookRequestFor(offer, "cancel-late"), CancellationToken.None));
        Time.SetUtcNow(new DateTimeOffset(2027, 2, 28, 9, 0, 0, TimeSpan.Zero));

        var outcome = Ok(await Port<ICancel>()!.CancelAsync(new CancelRequest(confirmation.SupplierBookingRef), CancellationToken.None));

        outcome.Status.ShouldBe(SupplierCancelStatus.PenaltyApplied);
        outcome.Penalty!.Value.Amount.ShouldBeGreaterThan(0);
        outcome.Penalty!.Value.Currency.ShouldBe("EUR");
        (outcome.Penalty.Value.Amount + outcome.Refund!.Value.Amount).ShouldBe(offer.Offer.Price.Amount);
    }

    [Fact]
    public async Task A_non_refundable_offer_costs_its_full_price_to_cancel()
    {
        var offer = await FindOfferAsync(refundable: false);
        var confirmation = Ok(await Port<IBook>()!.BookAsync(BookRequestFor(offer, "cancel-nonref"), CancellationToken.None));

        var outcome = Ok(await Port<ICancel>()!.CancelAsync(new CancelRequest(confirmation.SupplierBookingRef), CancellationToken.None));

        outcome.Status.ShouldBe(SupplierCancelStatus.PenaltyApplied);
        outcome.Penalty!.Value.Amount.ShouldBe(offer.Offer.Price.Amount);
    }

    [Fact]
    public async Task Cancelling_an_unknown_booking_is_a_not_found_error()
    {
        var result = await Port<ICancel>()!.CancelAsync(new CancelRequest("no-such-booking"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Kind.ShouldBe(SupplierErrorKind.NotFound);
    }

    [Fact]
    public async Task Malformed_booking_answers_become_a_failure_and_never_an_exception()
    {
        var offer = await FindOfferAsync();
        Faults.Set(new FaultProfile { MalformedRate = 1 });

        var result = await Port<IBook>()!.BookAsync(BookRequestFor(offer, "malformed"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Kind.ShouldBe(SupplierErrorKind.Unknown);
    }

    [Fact]
    public async Task A_cancelled_token_stops_a_booking_call()
    {
        var offer = await FindOfferAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Port<IBook>()!.BookAsync(BookRequestFor(offer, "cancelled"), cts.Token));
    }
}

public class SupplierABookingTests : BookingPortsContractTests
{
    protected override SupplierId Id => FakeSupplierIds.SupplierA;

    protected override SupplierCityCodes Codes => new("ALD", "XA");

    protected override SupplierCapabilities Expected =>
        SupplierCapabilities.RoomAvailability | SupplierCapabilities.PreBook | SupplierCapabilities.Book | SupplierCapabilities.Cancel | SupplierCapabilities.Retrieve;

    [Fact]
    public async Task Prebook_returns_the_current_price_and_sees_a_moved_one()
    {
        var offer = await FindOfferAsync();
        var preBook = Port<IPreBook>()!;

        var same = Ok(await preBook.PreBookAsync(new PreBookRequest(offer), CancellationToken.None));
        Faults.Set(new FaultProfile { PriceDriftPercent = 10 });
        var dearer = Ok(await preBook.PreBookAsync(new PreBookRequest(offer), CancellationToken.None));

        same.Price.ShouldBe(offer.Offer.Price);
        dearer.Price.Amount.ShouldBe(Math.Round(offer.Offer.Price.Amount * 1.10m, 2, MidpointRounding.AwayFromZero));
        same.ValidUntil.ShouldNotBeNull();
    }

    [Fact]
    public async Task Retrieve_finds_a_booking_by_its_own_reference_or_by_the_reference_the_hub_sent()
    {
        var offer = await FindOfferAsync();
        var confirmation = Ok(await Port<IBook>()!.BookAsync(BookRequestFor(offer, "find-me"), CancellationToken.None));
        var retrieve = Port<IRetrieve>()!;

        var byRef = Ok(await retrieve.RetrieveAsync(new RetrieveRequest(confirmation.SupplierBookingRef, null), CancellationToken.None));
        var byClient = Ok(await retrieve.RetrieveAsync(new RetrieveRequest(null, "find-me"), CancellationToken.None));
        var missing = await retrieve.RetrieveAsync(new RetrieveRequest(null, "never-booked"), CancellationToken.None);

        byRef.Status.ShouldBe(BookingStatus.Confirmed);
        byClient.SupplierBookingRef.ShouldBe(confirmation.SupplierBookingRef);
        missing.Error.Kind.ShouldBe(SupplierErrorKind.NotFound);
    }

    [Fact]
    public async Task Availability_lists_the_current_rooms_of_one_hotel()
    {
        var offer = await FindOfferAsync();

        var rooms = Ok(await Port<IRoomAvailability>()!.GetRoomsAsync(new RoomAvailabilityQuery(offer.Search, offer.Offer.SupplierHotelCode), CancellationToken.None));

        rooms.ShouldContain(r => r.OfferId == offer.Offer.OfferId && r.Price == offer.Offer.Price);
    }
}

public class SupplierBBookingTests : BookingPortsContractTests
{
    protected override SupplierId Id => FakeSupplierIds.SupplierB;

    protected override SupplierCityCodes Codes => new("5001", "900");

    protected override SupplierCapabilities Expected =>
        SupplierCapabilities.RoomAvailability | SupplierCapabilities.Book | SupplierCapabilities.Cancel | SupplierCapabilities.Retrieve;

    [Fact]
    public async Task Booking_is_asynchronous_it_is_accepted_as_pending_and_confirmed_by_polling()
    {
        var offer = await FindOfferAsync();

        var accepted = Ok(await Port<IBook>()!.BookAsync(BookRequestFor(offer, "async-ref"), CancellationToken.None));
        var retrieve = Port<IRetrieve>()!;
        var early = Ok(await retrieve.RetrieveAsync(new RetrieveRequest(accepted.SupplierBookingRef, null), CancellationToken.None));
        Time.Advance(TimeSpan.FromSeconds(1));
        var later = Ok(await retrieve.RetrieveAsync(new RetrieveRequest(accepted.SupplierBookingRef, null), CancellationToken.None));

        accepted.Status.ShouldBe(BookingStatus.Pending);
        early.Status.ShouldBe(BookingStatus.Pending);
        later.Status.ShouldBe(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task An_accepted_booking_can_still_end_up_failed()
    {
        var offer = await FindOfferAsync();
        Faults.Set(new FaultProfile { BookingFailRate = 1 });
        var accepted = Ok(await Port<IBook>()!.BookAsync(BookRequestFor(offer, "will-fail"), CancellationToken.None));
        Faults.Set(FaultProfile.None);

        Time.Advance(TimeSpan.FromSeconds(1));
        var state = Ok(await Port<IRetrieve>()!.RetrieveAsync(new RetrieveRequest(accepted.SupplierBookingRef, null), CancellationToken.None));

        state.Status.ShouldBe(BookingStatus.Failed);
    }

    [Fact]
    public async Task A_price_is_verified_through_availability_because_there_is_no_prebook()
    {
        var offer = await FindOfferAsync();
        Faults.Set(new FaultProfile { PriceDriftPercent = 10 });

        var rooms = Ok(await Port<IRoomAvailability>()!.GetRoomsAsync(new RoomAvailabilityQuery(offer.Search, offer.Offer.SupplierHotelCode), CancellationToken.None));

        rooms.Single(r => r.OfferId == offer.Offer.OfferId).Price.Amount
            .ShouldBe(Math.Round(offer.Offer.Price.Amount * 1.10m, 2, MidpointRounding.AwayFromZero), 0.011m);
    }

    [Fact]
    public async Task Retrieve_finds_a_booking_whose_answer_was_lost_by_the_reference_the_hub_sent()
    {
        var offer = await FindOfferAsync();
        Faults.Set(new FaultProfile { AcceptedButLostRate = 1 });
        (await Port<IBook>()!.BookAsync(BookRequestFor(offer, "lost-b"), CancellationToken.None)).IsFailure.ShouldBeTrue();
        Faults.Set(FaultProfile.None);

        var found = Ok(await Port<IRetrieve>()!.RetrieveAsync(new RetrieveRequest(null, "lost-b"), CancellationToken.None));
        var missing = await Port<IRetrieve>()!.RetrieveAsync(new RetrieveRequest(null, "unknown"), CancellationToken.None);

        found.SupplierReference.ShouldBe("lost-b");
        missing.Error.Kind.ShouldBe(SupplierErrorKind.NotFound);
    }
}

public class SupplierCBookingTests : BookingPortsContractTests
{
    protected override SupplierId Id => FakeSupplierIds.SupplierC;

    protected override SupplierCityCodes Codes => new("ALDERMOOR", "XAL");

    protected override SupplierCapabilities Expected =>
        SupplierCapabilities.PreBook | SupplierCapabilities.Book | SupplierCapabilities.Cancel;

    [Fact]
    public async Task Prebook_needs_the_session_from_the_search_and_extends_it()
    {
        var offer = await FindOfferAsync();
        offer.Offer.Extra.ShouldContainKey("sessionId");
        var preBook = Port<IPreBook>()!;

        Time.Advance(TimeSpan.FromSeconds(50));
        var quote = Ok(await preBook.PreBookAsync(new PreBookRequest(offer), CancellationToken.None));
        Time.Advance(TimeSpan.FromSeconds(50));
        var again = Ok(await preBook.PreBookAsync(new PreBookRequest(offer), CancellationToken.None));

        quote.Price.ShouldBe(offer.Offer.Price);
        again.Price.ShouldBe(offer.Offer.Price, "the first prebook kept the session alive past its original lifetime");
    }

    [Fact]
    public async Task An_expired_session_is_a_typed_error_for_prebook_and_for_booking()
    {
        var offer = await FindOfferAsync();
        Time.Advance(TimeSpan.FromSeconds(61));

        var preBook = await Port<IPreBook>()!.PreBookAsync(new PreBookRequest(offer), CancellationToken.None);
        var book = await Port<IBook>()!.BookAsync(BookRequestFor(offer, "late"), CancellationToken.None);

        preBook.Error.Kind.ShouldBe(SupplierErrorKind.SessionExpired);
        book.Error.Kind.ShouldBe(SupplierErrorKind.SessionExpired);
        Ledgers.Count(Id).ShouldBe(0);
    }

    [Fact]
    public async Task An_offer_without_its_session_cannot_be_booked()
    {
        var offer = await FindOfferAsync();
        var stripped = offer with { Offer = offer.Offer with { Extra = new Dictionary<string, string>() } };

        var result = await Port<IBook>()!.BookAsync(BookRequestFor(stripped, "no-session"), CancellationToken.None);

        result.Error.Kind.ShouldBe(SupplierErrorKind.InvalidRequest);
    }
}
