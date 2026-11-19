using Hub.Abstractions;
using Hub.Booking;
using Hub.Suppliers.Fakes;
using Shouldly;
using Xunit;

namespace Hub.Tests;

/// <summary>
/// The shown-equals-paid invariant under random sequences of quote, supplier price moves, time passing and booking
/// attempts (good, stale and wrong-amount): whatever happens, a booking the supplier recorded was made against the quote
/// that was current, for exactly the amount the client was shown for it and for the supplier price that quote carried.
/// </summary>
public class BookingInvariantTests(Xunit.ITestOutputHelper output)
{
    private static readonly SupplierId A = FakeSupplierIds.SupplierA;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task What_is_charged_is_always_what_was_last_shown_for_the_quote_that_was_booked(int seed)
    {
        await using var h = await BookingHarness.CreateAsync();
        var random = new Random(seed);
        var offer = await h.FindOfferAsync(A);
        var shown = new Dictionary<string, Money>();
        var quotesInOrder = new List<QuoteView>();
        QuoteView? latest = null;
        var bookingIds = new List<string>();
        var created = 0;
        var refused = 0;

        void Show(QuoteView view)
        {
            shown[view.QuoteId] = view.SellingPrice;
            if (quotesInOrder.All(q => q.QuoteId != view.QuoteId))
            {
                quotesInOrder.Add(view);
            }

            latest = view;
        }

        async Task AttemptAsync(QuoteView quote, decimal amount, string key, bool mustBeRefused)
        {
            var before = h.Ledgers.Count(A);
            var outcome = await h.Bookings.CreateAsync(h.Command(quote, key) with { Amount = amount }, default);
            if (outcome.CurrentQuote is { } current)
            {
                Show(current);
            }

            if (mustBeRefused)
            {
                outcome.Status.ShouldNotBe(BookingCreateStatus.Created, "a stale quote or a wrong amount must never be accepted");
            }

            if (outcome.Status == BookingCreateStatus.Created)
            {
                created++;
                bookingIds.Add(outcome.Booking!.Id);
                outcome.Booking.SellingAmount.ShouldBe(shown[outcome.Booking.QuoteId].Amount, "the booking carries the amount that was shown");
                if (outcome.Booking.Status != HubBookingStatus.Confirmed)
                {
                    h.Ledgers.Count(A).ShouldBe(before, "a booking that did not confirm left nothing at the supplier");
                }
            }
            else
            {
                refused++;
                h.Ledgers.Count(A).ShouldBe(before, "a refused booking leaves nothing at the supplier");
            }
        }

        for (var step = 0; step < 120; step++)
        {
            switch (random.Next(6))
            {
                case 0:
                case 1:
                    var quoted = await h.Quotes.QuoteAsync(BookingHarness.Tenant, offer.Offer.OfferId, default);
                    if (quoted.Quote is { } view)
                    {
                        Show(view);
                    }
                    else
                    {
                        offer = await h.FindOfferAsync(A);
                        latest = null;
                    }

                    break;
                case 2:
                    h.Faults.Get(A)!.Set(new FaultProfile { PriceDriftPercent = new[] { 0, 5, 10, -5, 20 }[random.Next(5)] });
                    break;
                case 3:
                    h.Time.Advance(TimeSpan.FromMinutes(random.Next(0, 13)));
                    break;
                case 4:
                    if (latest is { } good)
                    {
                        await AttemptAsync(good, good.SellingPrice.Amount, $"k-{seed}-{step}", mustBeRefused: false);
                    }

                    break;
                default:
                    if (quotesInOrder.Count > 1)
                    {
                        var stale = quotesInOrder[random.Next(quotesInOrder.Count - 1)];
                        await AttemptAsync(stale, shown[stale.QuoteId].Amount, $"s-{seed}-{step}", mustBeRefused: stale.QuoteId != latest?.QuoteId);
                    }
                    else if (latest is { } only)
                    {
                        await AttemptAsync(only, only.SellingPrice.Amount + 0.01m, $"w-{seed}-{step}", mustBeRefused: true);
                    }

                    break;
            }
        }

        // The ledgers of the supplier agree with the bookings and the quotes behind them.
        var supplierBookings = h.Ledgers.Bookings(A);
        supplierBookings.Count.ShouldBe(supplierBookings.Select(static b => b.ClientRef).Distinct().Count(), "no reference was booked twice");
        foreach (var id in bookingIds)
        {
            var booking = (await h.Bookings.GetAsync(BookingHarness.Tenant, id, default))!;
            var quote = (await h.QuoteStore.GetAsync(BookingHarness.Tenant, booking.QuoteId, default))!;
            var onSupplierSide = supplierBookings.SingleOrDefault(b => b.ClientRef == booking.SupplierReference);
            if (booking.Status == HubBookingStatus.Confirmed)
            {
                onSupplierSide.ShouldNotBeNull();
                onSupplierSide.Total.ShouldBe(quote.SupplierAmount, "the supplier was charged the supplier price of the booked quote");
                booking.SellingAmount.ShouldBe(shown[quote.Id].Amount, "and the client the amount shown for it");
                quote.Status.ShouldBe(QuoteStatus.Used);
            }
            else
            {
                onSupplierSide.ShouldBeNull();
            }
        }

        var confirmed = 0;
        foreach (var id in bookingIds)
        {
            confirmed += (await h.Bookings.GetAsync(BookingHarness.Tenant, id, default))!.Status == HubBookingStatus.Confirmed ? 1 : 0;
        }

        supplierBookings.Count.ShouldBe(confirmed, "every booking at the supplier belongs to a confirmed booking of the hub, and the other way round");
        (created + refused).ShouldBeGreaterThanOrEqualTo(5, "the random run attempted bookings");
        confirmed.ShouldBeGreaterThan(0, "the random run confirmed at least one booking, so the checks above had something to check");
        output.WriteLine($"seed {seed}: {created} bookings created ({confirmed} confirmed), {refused} refused");
    }
}
