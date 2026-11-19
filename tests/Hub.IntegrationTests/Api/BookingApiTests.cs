using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class BookingApiTests(HubApiFactory factory) : IClassFixture<HubApiFactory>
{
    private HttpClient Client => factory.ClientFor(HubApiFactory.ClientKey);

    private HttpClient Admin => factory.ClientFor(HubApiFactory.AdminKey);

    private async Task<(string OfferId, decimal Price)> FindOfferAsync(string supplier = "supplier-a", bool refundable = true)
    {
        var body = await HubApiFactory.ReadJsonAsync(await Client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody(suppliers: [supplier])));
        var offer = body["hotels"]!.AsArray().SelectMany(static h => h!["offers"]!.AsArray()).First(o => o!["refundable"]!.GetValue<bool>() == refundable)!;
        return (offer["offerId"]!.GetValue<string>(), offer["sellingPrice"]!["amount"]!.GetValue<decimal>());
    }

    private async Task<JsonNode> QuoteAsync(string offerId)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/quotes", new { offerId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await HubApiFactory.ReadJsonAsync(response);
    }

    private Task<HttpResponseMessage> BookAsync(JsonNode quote, string? key, object? overrides = null, decimal? amount = null, string guest = "Test Guest", string? mode = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings")
        {
            Content = JsonContent.Create(overrides ?? new
            {
                quoteId = quote["quoteId"]!.GetValue<string>(),
                amount = amount ?? quote["sellingPrice"]!["amount"]!.GetValue<decimal>(),
                currency = quote["sellingPrice"]!["currency"]!.GetValue<string>(),
                leadGuestName = guest,
                paymentMode = mode,
            }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return Client.SendAsync(request);
    }

    [Fact]
    public async Task Quote_book_replay_read_and_cancel_work_end_to_end()
    {
        var (offerId, price) = await FindOfferAsync();
        var quote = await QuoteAsync(offerId);
        quote["sellingPrice"]!["amount"]!.GetValue<decimal>().ShouldBe(price);
        quote["priceChanged"].ShouldBeNull();
        var key = $"api-flow-{Guid.NewGuid():N}";

        var created = await BookAsync(quote, key);
        var replay = await BookAsync(quote, key);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldStartWith("/api/v1/bookings/");
        created.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
        var booking = await HubApiFactory.ReadJsonAsync(created);
        booking["status"]!.GetValue<string>().ShouldBe("confirmed");
        booking["sellingPrice"]!["amount"]!.GetValue<decimal>().ShouldBe(price);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await HubApiFactory.ReadJsonAsync(replay))["id"]!.GetValue<string>().ShouldBe(booking["id"]!.GetValue<string>());

        var id = booking["id"]!.GetValue<string>();
        var read = await HubApiFactory.ReadJsonAsync(await Client.GetAsync($"/api/v1/bookings/{id}"));
        read["status"]!.GetValue<string>().ShouldBe("confirmed");
        var synced = await Client.PostAsync($"/api/v1/bookings/{id}/sync", null);
        synced.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cancelled = await HubApiFactory.ReadJsonAsync(await Client.PostAsync($"/api/v1/bookings/{id}/cancel", null));
        cancelled["status"]!.GetValue<string>().ShouldBeOneOf("cancelled", "penaltyApplied");
        var again = await HubApiFactory.ReadJsonAsync(await Client.PostAsync($"/api/v1/bookings/{id}/cancel", null));
        again["status"]!.GetValue<string>().ShouldBe("alreadyCancelled");
        (await HubApiFactory.ReadJsonAsync(await Client.GetAsync($"/api/v1/bookings/{id}")))["status"]!.GetValue<string>().ShouldBe("cancelled");
    }

    [Fact]
    public async Task The_idempotency_key_is_required_and_a_reused_key_with_another_payload_is_422()
    {
        var (offerId, _) = await FindOfferAsync();
        var quote = await QuoteAsync(offerId);
        var key = $"reuse-{Guid.NewGuid():N}";

        var missing = await BookAsync(quote, null);
        await BookAsync(quote, key);
        var reused = await BookAsync(quote, key, guest: "Another Guest");

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await HubApiFactory.ReadJsonAsync(reused))["type"]!.GetValue<string>().ShouldBe("urn:hub:problem:idempotency-key-reused");
    }

    [Fact]
    public async Task Booking_an_amount_other_than_the_one_shown_is_409_price_changed_with_the_quote()
    {
        var (offerId, _) = await FindOfferAsync();
        var quote = await QuoteAsync(offerId);

        var response = await BookAsync(quote, $"wrong-{Guid.NewGuid():N}", amount: quote["sellingPrice"]!["amount"]!.GetValue<decimal>() + 1m);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await HubApiFactory.ReadJsonAsync(response);
        problem["type"]!.GetValue<string>().ShouldBe("urn:hub:problem:price-changed");
        problem["currentQuote"]!["quoteId"]!.GetValue<string>().ShouldBe(quote["quoteId"]!.GetValue<string>());
        problem["currentQuote"]!["sellingPrice"]!["amount"]!.GetValue<decimal>().ShouldBe(quote["sellingPrice"]!["amount"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_price_that_moved_is_reported_by_the_quote_and_written_back_to_the_offer()
    {
        var (offerId, price) = await FindOfferAsync("supplier-b");
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-b", new { priceDriftPercent = 10 });
        try
        {
            var quote = await QuoteAsync(offerId);

            var changed = quote["priceChanged"]!;
            changed["from"]!["amount"]!.GetValue<decimal>().ShouldBe(price);
            changed["to"]!["amount"]!.GetValue<decimal>().ShouldBe(quote["sellingPrice"]!["amount"]!.GetValue<decimal>());
            quote["sellingPrice"]!["amount"]!.GetValue<decimal>().ShouldBeGreaterThan(price);
            var later = await HubApiFactory.ReadJsonAsync(await Client.GetAsync($"/api/v1/offers/{Uri.EscapeDataString(offerId)}"));
            later["sellingPrice"]!["amount"]!.GetValue<decimal>().ShouldBe(quote["sellingPrice"]!["amount"]!.GetValue<decimal>(), "later reads show the new price");
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-b");
        }
    }

    [Fact]
    public async Task Quoting_validates_the_request_and_reports_unknown_offers()
    {
        (await Client.PostAsJsonAsync("/api/v1/quotes", new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var unknown = await Client.PostAsJsonAsync("/api/v1/quotes", new { offerId = "nothing-like-it" });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await HubApiFactory.ReadJsonAsync(unknown))["type"]!.GetValue<string>().ShouldBe("urn:hub:problem:offer-not-found");
        (await Client.GetAsync("/api/v1/offers/nothing-like-it")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_tenants_key_cannot_see_the_booking_and_payments_need_an_admin_key()
    {
        var (offerId, _) = await FindOfferAsync();
        var quote = await QuoteAsync(offerId);
        var booking = await HubApiFactory.ReadJsonAsync(await BookAsync(quote, $"tenant-{Guid.NewGuid():N}"));
        var id = booking["id"]!.GetValue<string>();

        (await factory.ClientFor(HubApiFactory.OtherTenantKey).GetAsync($"/api/v1/bookings/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Client.PostAsync($"/api/v1/bookings/{id}/payments", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var paid = await Admin.PostAsync($"/api/v1/bookings/{id}/payments", null);
        paid.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HubApiFactory.ReadJsonAsync(paid))["paidAt"].ShouldNotBeNull();
        (await factory.CreateClient().GetAsync($"/api/v1/bookings/{id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pay_later_is_refused_for_an_offer_that_is_not_refundable()
    {
        var (offerId, _) = await FindOfferAsync(refundable: false);
        var quote = await QuoteAsync(offerId);

        var response = await BookAsync(quote, $"later-{Guid.NewGuid():N}", mode: "payLater");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await HubApiFactory.ReadJsonAsync(response))["type"]!.GetValue<string>().ShouldBe("urn:hub:problem:pay-later-not-allowed");
    }

    [Fact]
    public async Task Pay_later_is_allowed_for_a_refundable_offer_and_stores_the_deadline_and_the_pay_by_moment()
    {
        var (offerId, _) = await FindOfferAsync();
        var quote = await QuoteAsync(offerId);
        quote["refundable"]!.GetValue<bool>().ShouldBeTrue();
        quote["freeCancellationUntil"]!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2027, 2, 27, 0, 0, 0, TimeSpan.Zero));

        var response = await BookAsync(quote, $"later-{Guid.NewGuid():N}", mode: "payLater");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var booking = await HubApiFactory.ReadJsonAsync(response);
        booking["paymentMode"]!.GetValue<string>().ShouldBe("payLater");
        booking["paidAt"].ShouldBeNull();
        booking["payBy"]!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2027, 2, 26, 0, 0, 0, TimeSpan.Zero), "a day before the free window closes");
    }

    [Fact]
    public async Task A_lost_booking_answer_is_unknown_and_syncing_it_confirms_it_without_booking_again()
    {
        var (offerId, _) = await FindOfferAsync();
        var quote = await QuoteAsync(offerId);
        await HubApiFactory.SetFaultsAsync(Admin, "supplier-a", new { acceptedButLostRate = 1 });
        string id;
        try
        {
            var created = await HubApiFactory.ReadJsonAsync(await BookAsync(quote, $"lost-{Guid.NewGuid():N}"));
            created["status"]!.GetValue<string>().ShouldBe("unknown");
            id = created["id"]!.GetValue<string>();
        }
        finally
        {
            await HubApiFactory.ResetFaultsAsync(Admin, "supplier-a");
        }

        var cancelUnknown = await Client.PostAsync($"/api/v1/bookings/{id}/cancel", null);
        var synced = await HubApiFactory.ReadJsonAsync(await Client.PostAsync($"/api/v1/bookings/{id}/sync", null));

        cancelUnknown.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        synced["status"]!.GetValue<string>().ShouldBe("confirmed");
    }
}
