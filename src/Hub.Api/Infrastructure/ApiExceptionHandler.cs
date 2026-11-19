using Hub.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Hub.Api;

/// <summary>Turns known domain exceptions into RFC 7807 problem details; anything else becomes a generic 500 with no internals.</summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            InvalidSearchRequestException invalid => new HttpValidationProblemDetails(
                invalid.Errors.ToDictionary(static e => Camel(e.Key), static e => e.Value, StringComparer.Ordinal))
            {
                Title = "The request is not valid.",
                Status = StatusCodes.Status400BadRequest,
            },
            SearchRunAccessDeniedException denied => new ProblemDetails
            {
                Title = "The search belongs to a different caller.",
                Detail = $"Search '{denied.RunId}' cannot be replaced or cancelled by this caller.",
                Status = StatusCodes.Status403Forbidden,
            },
            SearchRunConflictException conflict => new ProblemDetails
            {
                Title = "The search id is already in use.",
                Detail = $"A search with id '{conflict.RunId}' is still running.",
                Status = StatusCodes.Status409Conflict,
            },
            EventsEvictedException => new ProblemDetails
            {
                Title = "The events after that id are no longer buffered.",
                Status = StatusCodes.Status410Gone,
            },
            BadHttpRequestException bad => new ProblemDetails
            {
                Title = "The request could not be read.",
                Status = bad.StatusCode,
            },
            _ => null,
        };

        if (problem is null)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
            return false;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    public static string Camel(string name) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name);
}
