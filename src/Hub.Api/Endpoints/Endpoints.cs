using System.Security.Claims;
using System.Text.Json;
using Hub.Abstractions;
using Hub.Core;
using Hub.Mapping;
using Hub.Suppliers.Fakes;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Hub.Api;

internal static class SearchEndpoints
{
    public static void MapSearchEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/searches").WithTags("Searches").RequireAuthorization(Policies.Client);

        group.MapPost("/", SearchAsync)
            .WithName("SearchHotels")
            .WithSummary("Search all eligible suppliers and return the merged result once they have answered or the deadline passes.")
            .Produces<SearchResult>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/stream", StreamAsync)
            .WithName("StreamHotelSearch")
            .WithSummary("Search and stream updates as server-sent events while suppliers answer.")
            .Produces(StatusCodes.Status200OK, typeof(string), "text/event-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{searchId}/events", ResumeAsync)
            .WithName("ResumeHotelSearchStream")
            .WithSummary("Replay the buffered events of a search after the Last-Event-ID header, then continue live until it completes.")
            .Produces(StatusCodes.Status200OK, typeof(string), "text/event-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);
        group.MapDelete("/{searchId}", Cancel)
            .WithName("CancelHotelSearch")
            .WithSummary("Cancel a running search started by the same caller.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> SearchAsync(
        SearchRequestDto? body,
        ClaimsPrincipal user,
        SearchOrchestrator orchestrator,
        IOptions<SearchOptions> defaults,
        CancellationToken cancellationToken)
    {
        if (!SearchRequestMapper.TryMap(body, defaults.Value, out var mapped, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var result = await orchestrator.SearchToCompletionAsync(mapped.Request, user.ToOwner(), mapped.Options, cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> StreamAsync(
        SearchRequestDto? body,
        HttpContext http,
        ClaimsPrincipal user,
        SearchStreams streams,
        IOptions<SearchOptions> defaults,
        IOptions<SseOptions> sse,
        IOptions<JsonOptions> json,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!SearchRequestMapper.TryMap(body, defaults.Value, out var mapped, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var stream = streams.Start(mapped.Request, user.ToOwner(), mapped.Options);
        using var listener = stream.Attach();
        await ServerSentEvents.WriteAsync(http, stream.Log.ReadAsync(0, cancellationToken), json.Value.SerializerOptions, sse.Value.HeartbeatInterval, time, cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }

    private static async Task<IResult> ResumeAsync(
        string searchId,
        HttpContext http,
        ClaimsPrincipal user,
        SearchStreams streams,
        IOptions<SseOptions> sse,
        IOptions<JsonOptions> json,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        long afterId = 0;
        if (http.Request.Headers.TryGetValue("Last-Event-ID", out var header) && header.Count > 0
            && !long.TryParse(header[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out afterId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["Last-Event-ID"] = ["Last-Event-ID must be the number of the last event received."] });
        }

        if (streams.Find(searchId) is not { } stream)
        {
            return Results.Problem(
                title: "No search with that id is running or kept for replay.", detail: "It may have finished and been dropped from the replay buffer.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (stream.Owner != user.ToOwner())
        {
            return Results.Problem(title: "The search belongs to a different caller.", statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            stream.Log.EnsureResumable(afterId);
        }
        catch (EventsEvictedException ex)
        {
            return Results.Problem(
                title: "The events after that id are no longer buffered.",
                detail: $"The oldest retained event is {ex.FirstRetained}. Start a new search or read the stream from the beginning of the retained window.",
                statusCode: StatusCodes.Status410Gone,
                extensions: new Dictionary<string, object?> { ["oldestRetainedEventId"] = ex.FirstRetained });
        }

        using var listener = stream.Attach();
        await ServerSentEvents.WriteAsync(http, stream.Log.ReadAsync(afterId, cancellationToken), json.Value.SerializerOptions, sse.Value.HeartbeatInterval, time, cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }

    private static IResult Cancel(string searchId, ClaimsPrincipal user, SearchRunRegistry runs) =>
        runs.Cancel(searchId, user.ToOwner()) switch
        {
            CancelOutcome.Cancelled => Results.NoContent(),
            CancelOutcome.NotOwner => Results.Problem(
                title: "The search belongs to a different caller.", statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(
                title: "No running search has that id.", detail: "It may have finished already.", statusCode: StatusCodes.Status404NotFound),
        };
}

internal static class SupplierEndpoints
{
    public static void MapSupplierEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/suppliers", List)
            .WithTags("Suppliers")
            .WithName("ListSuppliers")
            .WithSummary("List suppliers with their capabilities, whether the caller's tenant has them enabled, circuit state and in-flight calls.")
            .RequireAuthorization(Policies.Client)
            .Produces<SupplierInfo[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapPut("/admin/suppliers/{supplierId}/faults", SetFaults)
            .WithTags("Admin")
            .WithName("SetSupplierFaults")
            .WithSummary("Replace the fault profile of a fake supplier (latency, errors, malformed payloads, hangs).")
            .RequireAuthorization(Policies.Admin)
            .Produces<FaultProfile>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static SupplierInfo[] List(ClaimsPrincipal user, ISupplierRegistry registry, ITenantSupplierSelector selector, ISupplierGateway gateway)
    {
        var tenant = user.ToOwner().Tenant;
        return registry.Suppliers
            .Select(entry =>
            {
                var status = gateway.GetStatus(entry.Id);
                var d = entry.Descriptor;
                return new SupplierInfo(
                    entry.Id.Value,
                    d.SearchStyle,
                    Enum.GetValues<SupplierCapabilities>().Where(c => c != SupplierCapabilities.None && d.Capabilities.HasFlag(c)).Select(static c => JsonNamingPolicy.CamelCase.ConvertName(c.ToString())).ToArray(),
                    (int)d.DefaultTimeout.TotalMilliseconds,
                    d.MaxConcurrency,
                    selector.IsEnabled(tenant, entry.Id),
                    status.Circuit,
                    status.InFlight);
            })
            .ToArray();
    }

    private static IResult SetFaults(string supplierId, FaultProfile? profile, FakeSupplierFaults faults)
    {
        if (!SupplierId.TryParse(supplierId, out var id) || faults.Get(id) is not { } controller)
        {
            return Results.Problem(title: "No fake supplier has that id.", statusCode: StatusCodes.Status404NotFound);
        }

        if (profile is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A fault profile is required."] });
        }

        var errors = profile.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["faults"] = errors });
        }

        controller.Set(profile);
        return Results.Ok(controller.Current);
    }
}

internal static class MappingEndpoints
{
    public static void MapMappingEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/mapping").WithTags("Mapping").RequireAuthorization(Policies.Admin);

        group.MapPost("/identities", AddIdentityAsync)
            .WithName("AddHotelIdentity")
            .WithSummary("Attach a supplier hotel code to a canonical hotel, minting a new canonical id when none is given.")
            .Produces<IdentityResponse>(StatusCodes.Status201Created)
            .Produces<IdentityResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/aliases", SetAliasAsync)
            .WithName("SetHotelAlias")
            .WithSummary("Record that a canonical id was retired in favour of a surviving one.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/hotels/{canonicalId}", GetHotelAsync)
            .WithName("GetCanonicalHotel")
            .WithSummary("Show the supplier records behind a canonical hotel, following merge history.")
            .Produces<CanonicalHotelResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> AddIdentityAsync(AddIdentityDto? body, HotelMappingService mapping, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var supplier = default(SupplierId);
        if (body is null || !SupplierId.TryParse(body.Supplier, out supplier))
        {
            errors["supplier"] = ["supplier must be a valid supplier id."];
        }

        if (body is null || string.IsNullOrWhiteSpace(body.SupplierCode) || body.SupplierCode.Length > 128)
        {
            errors["supplierCode"] = ["supplierCode is required and at most 128 characters."];
        }

        if (body?.CanonicalId is { } requested && !CanonicalIds.TryParse(requested, out _))
        {
            errors["canonicalId"] = ["canonicalId is not a canonical hotel id."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await mapping.AddIdentityAsync(new AddIdentityRequest(supplier, body!.SupplierCode!, body.CanonicalId), cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            AddIdentityStatus.Created => Results.Created($"/api/v1/mapping/hotels/{result.CanonicalId}", new IdentityResponse(result.CanonicalId!, "created")),
            AddIdentityStatus.AlreadyExists => Results.Ok(new IdentityResponse(result.CanonicalId!, "alreadyExists")),
            AddIdentityStatus.Conflict => Results.Problem(
                title: "The supplier code belongs to a different canonical hotel.",
                detail: $"It is already registered under '{result.CanonicalId}'.",
                statusCode: StatusCodes.Status409Conflict),
            AddIdentityStatus.CapExceeded => Results.Problem(
                title: "The canonical hotel already owns the maximum number of supplier records.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.Problem(title: "Unknown canonical id.", statusCode: StatusCodes.Status404NotFound),
        };
    }

    private static async Task<IResult> SetAliasAsync(SetAliasDto? body, HotelMappingService mapping, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body is null || !CanonicalIds.TryParse(body.RetiredId, out _))
        {
            errors["retiredId"] = ["retiredId must be a canonical hotel id."];
        }

        if (body is null || !CanonicalIds.TryParse(body.SurvivorId, out _))
        {
            errors["survivorId"] = ["survivorId must be a canonical hotel id."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return await mapping.SetAliasAsync(body!.RetiredId!, body.SurvivorId!, cancellationToken).ConfigureAwait(false) switch
        {
            SetAliasStatus.Set => Results.NoContent(),
            SetAliasStatus.SelfAlias => Results.ValidationProblem(new Dictionary<string, string[]> { ["survivorId"] = ["A hotel cannot be an alias of itself."] }),
            SetAliasStatus.WouldCreateCycle => Results.Problem(
                title: "The alias would create a cycle.", detail: "The survivor already resolves back to the retired id.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(title: "Unknown canonical id.", detail: "Both ids must belong to known canonical hotels.", statusCode: StatusCodes.Status404NotFound),
        };
    }

    private static async Task<IResult> GetHotelAsync(string canonicalId, HotelMappingService mapping, CancellationToken cancellationToken)
    {
        var view = await mapping.GetHotelAsync(canonicalId, cancellationToken).ConfigureAwait(false);
        return view is null
            ? Results.Problem(title: "Unknown canonical id.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new CanonicalHotelResponse(
                view.RequestedId,
                view.CanonicalId,
                view.Sources.Select(static s => new SourceDto(s.Supplier.Value, s.SupplierCode)).ToList(),
                view.RetiredIds));
    }
}
