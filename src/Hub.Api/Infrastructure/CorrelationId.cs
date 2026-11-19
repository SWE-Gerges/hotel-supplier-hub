namespace Hub.Api;

/// <summary>
/// Accepts the caller's <c>X-Correlation-Id</c> (or generates one), echoes it on the response, and adds it to the log scope
/// of everything that runs for the request.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied) && IsAcceptable(supplied.ToString())
            ? supplied.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(
            static state =>
            {
                var (response, id) = ((HttpResponse, string))state;
                response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            },
            (context.Response, correlationId));

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>Caller-supplied ids end up in logs and headers, so only short, plain identifiers are trusted.</summary>
    private static bool IsAcceptable(string value) =>
        value.Length is >= 1 and <= 64
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}

internal static class HttpContextCorrelationExtensions
{
    public static string? GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var value) ? value as string : null;
}
