using System.Text.Json;
using Hub.Mapping;

namespace Hub.Api;

public sealed record CrosswalkRunResponse(IReadOnlyList<CrosswalkSupplierRun> Suppliers, int Accepted, int Reviewed);

public sealed record CrosswalkCandidateDto(string CanonicalCityId, double Score);

public sealed record ReviewResponse(
    long Id,
    string Supplier,
    string SupplierCityCode,
    string SupplierCityName,
    string SupplierCountryCode,
    string? SupplierRegion,
    string Status,
    string? ProposedCityId,
    double Score,
    double RunnerUpScore,
    string Reason,
    IReadOnlyList<CrosswalkCandidateDto> Candidates,
    string? ResolvedCityId);

public sealed record CrosswalkEntryResponse(string Supplier, string SupplierCityCode, string SupplierCountryCode, string CanonicalCityId, double Score, string Source);

/// <param name="CanonicalCityId">The canonical city to map to; the review's own proposal when left out.</param>
public sealed record ApproveReviewDto(string? CanonicalCityId);

internal static class CrosswalkEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapCrosswalkEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/mapping/crosswalk").WithTags("Crosswalk").RequireAuthorization(Policies.Admin);
        group.MapPost("/runs", RunAsync)
            .WithName("RunCrosswalk")
            .WithSummary("Match every supplier's cities to canonical cities. Accepts clear matches, queues the rest for review, and changes nothing that is already decided.")
            .Produces<CrosswalkRunResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapGet("/entries", ListEntriesAsync)
            .WithName("ListCrosswalkEntries")
            .WithSummary("The supplier city codes known to mean a canonical city, optionally for one supplier.")
            .Produces<CrosswalkEntryResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapGet("/review", ListReviewAsync)
            .WithName("ListCrosswalkReview")
            .WithSummary("Supplier cities waiting for a person to decide (status pending by default).")
            .Produces<ReviewResponse[]>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        group.MapPost("/review/{id:long}/approve", ApproveAsync)
            .WithName("ApproveCrosswalkReview")
            .WithSummary("Map the supplier city to a canonical city (the proposal, or the one named in the body).")
            .Produces<ReviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        group.MapPost("/review/{id:long}/reject", RejectAsync)
            .WithName("RejectCrosswalkReview")
            .WithSummary("Decide that the supplier city is not any canonical city; later runs leave it alone.")
            .Produces<ReviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> RunAsync(CrosswalkService service, CancellationToken cancellationToken)
    {
        var summary = await service.RunAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new CrosswalkRunResponse(summary.Suppliers, summary.Accepted, summary.Reviewed));
    }

    private static async Task<IResult> ListEntriesAsync(string? supplier, CrosswalkService service, CancellationToken cancellationToken) =>
        Results.Ok((await service.GetEntriesAsync(supplier, cancellationToken).ConfigureAwait(false))
            .Select(static e => new CrosswalkEntryResponse(e.Supplier, e.SupplierCityCode, e.SupplierCountryCode, e.CanonicalCityId, e.Score, e.Source)));

    private static async Task<IResult> ListReviewAsync(string? status, string? supplier, CrosswalkService service, CancellationToken cancellationToken)
    {
        ReviewStatus? filter = ReviewStatus.Pending;
        if (status is not null && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<ReviewStatus>(status, ignoreCase: true, out var parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["status must be pending, approved, rejected or all."] });
            }

            filter = parsed;
        }
        else if (status is not null)
        {
            filter = null;
        }

        return Results.Ok((await service.GetReviewsAsync(filter, supplier, cancellationToken).ConfigureAwait(false)).Select(ToResponse));
    }

    private static async Task<IResult> ApproveAsync(long id, ApproveReviewDto? body, CrosswalkService service, CancellationToken cancellationToken) =>
        Respond(await service.ApproveAsync(id, body?.CanonicalCityId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> RejectAsync(long id, CrosswalkService service, CancellationToken cancellationToken) =>
        Respond(await service.RejectAsync(id, cancellationToken).ConfigureAwait(false));

    private static IResult Respond(ReviewResult result) => result.Resolution switch
    {
        ReviewResolution.Done => Results.Ok(ToResponse(result.Review!)),
        ReviewResolution.NotFound => Results.Problem(title: "No such review.", statusCode: StatusCodes.Status404NotFound),
        ReviewResolution.UnknownCity => Results.Problem(title: "The canonical city is not known.", detail: result.Detail, statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Results.Problem(title: "The review cannot be decided that way.", detail: result.Detail, statusCode: StatusCodes.Status409Conflict),
    };

    private static ReviewResponse ToResponse(CrosswalkReview r) => new(
        r.Id, r.Supplier, r.SupplierCityCode, r.SupplierCityName, r.SupplierCountryCode, r.SupplierRegion, r.Status.ToString().ToLowerInvariant(),
        r.ProposedCityId, Math.Round(r.Score, 3), Math.Round(r.RunnerUpScore, 3), r.Reason,
        JsonSerializer.Deserialize<List<CrosswalkCandidateDto>>(r.CandidatesJson, Json) ?? [], r.ResolvedCityId);
}
