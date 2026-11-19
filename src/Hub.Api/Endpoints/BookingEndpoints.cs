using System.Security.Claims;
using Hub.Booking;
using Hub.Core;

namespace Hub.Api;

internal static class BookingEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    private const int MaxKeyLength = 128;

    public static void MapBookingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/offers/{offerId}", GetOffer)
            .WithTags("Quotes")
            .WithName("GetStoredOffer")
            .WithSummary("Read an offer from a recent search as the hub holds it now, including any price a re-quote wrote back.")
            .RequireAuthorization(Policies.Client)
            .Produces<StoredOfferResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/quotes", CreateQuoteAsync)
            .WithTags("Quotes")
            .WithName("CreateQuote")
            .WithSummary("Re-price an offer with its supplier and return the amount that can be booked, and until when.")
            .RequireAuthorization(Policies.Client)
            .Produces<QuoteResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        var group = api.MapGroup("/bookings").WithTags("Bookings").RequireAuthorization(Policies.Client);
        group.MapPost("/", CreateBookingAsync)
            .WithName("CreateBooking")
            .WithSummary("Book against a live quote and the exact amount it showed. Needs an Idempotency-Key header; repeating the request returns the first outcome.")
            .Produces<BookingResponse>(StatusCodes.Status201Created)
            .Produces<BookingResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapGet("/{id}", GetBookingAsync)
            .WithName("GetBooking")
            .WithSummary("Read a booking.")
            .Produces<BookingResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{id}/sync", SyncBookingAsync)
            .WithName("SyncBooking")
            .WithSummary("Ask the supplier what it holds for a pending or unknown booking and update the booking from the answer.")
            .Produces<BookingResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{id}/cancel", CancelBookingAsync)
            .WithName("CancelBooking")
            .WithSummary("Cancel a booking. The answer says cancelled, penaltyApplied, alreadyCancelled or failed, whatever the supplier answered.")
            .Produces<CancelResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPost("/bookings/{id}/payments", MarkPaidAsync)
            .WithTags("Bookings")
            .WithName("MarkBookingPaid")
            .WithSummary("Mark a booking as paid (admin and demo use). A paid pay-later booking is no longer cancelled by the expiry service.")
            .RequireAuthorization(Policies.Admin)
            .Produces<BookingResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static IResult GetOffer(string offerId, ClaimsPrincipal user, IOfferStore offers) =>
        offers.Get(user.ToOwner().Tenant, offerId) is { } stored
            ? Results.Ok(new StoredOfferResponse(
                stored.Offer.OfferId,
                stored.Offer.Supplier.Value,
                stored.HotelName,
                stored.CanonicalId,
                stored.Offer.RoomName,
                stored.Offer.Board,
                stored.Offer.Refundable && stored.Offer.PolicyKnown,
                stored.Offer.RefundableUntil,
                stored.Offer.SellingPrice,
                stored.Offer.CancellationTiers))
            : Results.Problem(
                type: Problems.OfferNotFound, title: "The offer is unknown or no longer kept.", detail: "Search again to get current offers.",
                statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> CreateQuoteAsync(QuoteRequestDto? body, ClaimsPrincipal user, QuoteService quotes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body?.OfferId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["offerId"] = ["offerId is required."] });
        }

        var outcome = await quotes.QuoteAsync(user.ToOwner().Tenant, body.OfferId, cancellationToken).ConfigureAwait(false);
        if (outcome.Quote is { } quote)
        {
            return Results.Ok(BookingMapper.ToResponse(quote));
        }

        return outcome.Failure switch
        {
            QuoteFailure.OfferNotFound => Results.Problem(type: Problems.OfferNotFound, title: "The offer is unknown or no longer kept.", detail: outcome.Detail, statusCode: StatusCodes.Status404NotFound),
            QuoteFailure.SoldOut => Results.Problem(type: Problems.SoldOut, title: "The offer is no longer available.", detail: outcome.Detail, statusCode: StatusCodes.Status409Conflict),
            QuoteFailure.SessionExpired => Results.Problem(type: Problems.OfferExpired, title: "The offer has expired.", detail: outcome.Detail, statusCode: StatusCodes.Status410Gone),
            QuoteFailure.Unsupported or QuoteFailure.NotPriceable => Results.Problem(type: Problems.Unsupported, title: "The offer cannot be quoted.", detail: outcome.Detail, statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.Problem(type: Problems.SupplierFailed, title: "The supplier could not re-price the offer.", detail: outcome.Detail, statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private static async Task<IResult> CreateBookingAsync(
        CreateBookingDto? body, HttpContext http, ClaimsPrincipal user, BookingProcessor processor, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength || key.Any(static c => c < ' ' || c > '~'))
        {
            errors[IdempotencyKeyHeader] = [$"The {IdempotencyKeyHeader} header is required: 1 to {MaxKeyLength} printable characters."];
        }

        if (string.IsNullOrWhiteSpace(body?.QuoteId))
        {
            errors["quoteId"] = ["quoteId is required."];
        }

        if (body?.Amount is null or < 0)
        {
            errors["amount"] = ["amount is required: the selling amount the quote showed."];
        }

        if (body?.Currency is not { Length: 3 } currency || !currency.All(static c => c is >= 'A' and <= 'Z'))
        {
            errors["currency"] = ["currency must be a three-letter upper-case code."];
        }

        if (string.IsNullOrWhiteSpace(body?.LeadGuestName) || body.LeadGuestName.Length > 200)
        {
            errors["leadGuestName"] = ["leadGuestName is required (at most 200 characters)."];
        }

        var mode = PaymentMode.PayNow;
        if (body?.PaymentMode is { } modeText && !Enum.TryParse(modeText, ignoreCase: true, out mode))
        {
            errors["paymentMode"] = ["paymentMode must be payNow or payLater."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var outcome = await processor.CreateAsync(
            new CreateBookingCommand(user.ToOwner().Tenant, key, body!.QuoteId!, body.Amount!.Value, body.Currency!, body.LeadGuestName!.Trim(), mode),
            cancellationToken).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case BookingCreateStatus.Created:
                return Results.Created($"/api/v1/bookings/{outcome.Booking!.Id}", BookingMapper.ToResponse(outcome.Booking));
            case BookingCreateStatus.Replayed:
                http.Response.Headers[ReplayedHeader] = "true";
                return Results.Ok(BookingMapper.ToResponse(outcome.Booking!));
            case BookingCreateStatus.KeyReused:
                return Results.Problem(type: Problems.IdempotencyKeyReused, title: "The idempotency key was used for a different request.", detail: outcome.Detail, statusCode: StatusCodes.Status422UnprocessableEntity);
            case BookingCreateStatus.QuoteNotFound:
                return Results.Problem(type: Problems.QuoteNotFound, title: "No such quote.", detail: outcome.Detail, statusCode: StatusCodes.Status404NotFound);
            case BookingCreateStatus.PayLaterNotAllowed:
                return Results.Problem(type: Problems.PayLaterNotAllowed, title: "Pay later is not allowed for this offer.", detail: outcome.Detail, statusCode: StatusCodes.Status422UnprocessableEntity);
            case BookingCreateStatus.Unsupported:
                return Results.Problem(type: Problems.Unsupported, title: "The supplier cannot take bookings.", detail: outcome.Detail, statusCode: StatusCodes.Status422UnprocessableEntity);
            default:
                var extensions = new Dictionary<string, object?>();
                if (outcome.CurrentQuote is { } current)
                {
                    extensions["currentQuote"] = BookingMapper.ToResponse(current);
                }

                return outcome.Status == BookingCreateStatus.PriceChanged
                    ? Results.Problem(type: Problems.PriceChanged, title: "The price changed.", detail: outcome.Detail, statusCode: StatusCodes.Status409Conflict, extensions: extensions)
                    : Results.Problem(type: Problems.QuoteExpired, title: "The quote can no longer be booked.", detail: outcome.Detail, statusCode: StatusCodes.Status409Conflict, extensions: extensions);
        }
    }

    private static async Task<IResult> GetBookingAsync(string id, ClaimsPrincipal user, BookingProcessor processor, CancellationToken cancellationToken) =>
        await processor.GetAsync(user.ToOwner().Tenant, id, cancellationToken).ConfigureAwait(false) is { } booking
            ? Results.Ok(BookingMapper.ToResponse(booking))
            : BookingNotFound();

    private static async Task<IResult> SyncBookingAsync(string id, ClaimsPrincipal user, BookingProcessor processor, CancellationToken cancellationToken) =>
        await processor.SyncAsync(user.ToOwner().Tenant, id, cancellationToken).ConfigureAwait(false) is { } booking
            ? Results.Ok(BookingMapper.ToResponse(booking))
            : BookingNotFound();

    private static async Task<IResult> CancelBookingAsync(string id, ClaimsPrincipal user, BookingProcessor processor, CancellationToken cancellationToken)
    {
        var result = await processor.CancelAsync(user.ToOwner().Tenant, id, null, cancellationToken).ConfigureAwait(false);
        return result.Refusal switch
        {
            BookingCancelRefusal.NotFound => BookingNotFound(),
            BookingCancelRefusal.NotCancellable => Results.Problem(
                type: Problems.NotCancellable, title: "The booking cannot be cancelled in its current state.",
                detail: $"The booking is {BookingMapper.Camel(result.Booking!.Status.ToString())}; sync it first if its outcome is not known.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Ok(BookingMapper.ToResponse(result.Outcome!)),
        };
    }

    private static async Task<IResult> MarkPaidAsync(string id, ClaimsPrincipal user, BookingProcessor processor, CancellationToken cancellationToken)
    {
        var (booking, accepted) = await processor.MarkPaidAsync(user.ToOwner().Tenant, id, cancellationToken).ConfigureAwait(false);
        if (booking is null)
        {
            return BookingNotFound();
        }

        return accepted
            ? Results.Ok(BookingMapper.ToResponse(booking))
            : Results.Problem(type: Problems.NotCancellable, title: "The booking cannot be paid.", detail: $"The booking is {BookingMapper.Camel(booking.Status.ToString())}.", statusCode: StatusCodes.Status409Conflict);
    }

    private static IResult BookingNotFound() =>
        Results.Problem(type: Problems.BookingNotFound, title: "No such booking.", statusCode: StatusCodes.Status404NotFound);
}

/// <summary>Problem types the API uses beyond the generic status codes, so clients can tell the cases apart.</summary>
internal static class Problems
{
    public const string OfferNotFound = "urn:hub:problem:offer-not-found";
    public const string OfferExpired = "urn:hub:problem:offer-expired";
    public const string SoldOut = "urn:hub:problem:sold-out";
    public const string SupplierFailed = "urn:hub:problem:supplier-failed";
    public const string Unsupported = "urn:hub:problem:unsupported";
    public const string PriceChanged = "urn:hub:problem:price-changed";
    public const string QuoteExpired = "urn:hub:problem:quote-expired";
    public const string QuoteNotFound = "urn:hub:problem:quote-not-found";
    public const string IdempotencyKeyReused = "urn:hub:problem:idempotency-key-reused";
    public const string PayLaterNotAllowed = "urn:hub:problem:pay-later-not-allowed";
    public const string BookingNotFound = "urn:hub:problem:booking-not-found";
    public const string NotCancellable = "urn:hub:problem:booking-state-conflict";
}
