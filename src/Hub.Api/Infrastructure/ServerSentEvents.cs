using System.Text;
using System.Text.Json;
using Hub.Core;

namespace Hub.Api;

/// <summary>Writes search updates as a <c>text/event-stream</c> with heartbeat comments while the stream is quiet.</summary>
internal static class ServerSentEvents
{
    public const string SearchIdHeader = "X-Search-Id";

    /// <summary>
    /// Streams <paramref name="updates"/>. Nothing is written, not even headers, until the first update (or the end of the
    /// stream) is known, so refusals raised while the search starts can still be answered as problem details.
    /// </summary>
    public static async Task WriteAsync(
        HttpContext http,
        IAsyncEnumerable<SequencedUpdate> updates,
        JsonSerializerOptions json,
        TimeSpan heartbeat,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // A separate token lets this method stop the search itself, whatever ended the stream.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = updates.GetAsyncEnumerator(stop.Token);
        Task<bool>? pending = null;
        try
        {
            var hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);

            var response = http.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";
            if (hasCurrent)
            {
                response.Headers[SearchIdHeader] = enumerator.Current.Update.SearchId;
            }

            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await response.StartAsync(cancellationToken).ConfigureAwait(false);

            while (hasCurrent)
            {
                await WriteEventAsync(response, enumerator.Current, json, cancellationToken).ConfigureAwait(false);

                pending = enumerator.MoveNextAsync().AsTask();
                while (!pending.IsCompleted)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var timer = Task.Delay(heartbeat, time, wait.Token);
                    if (await Task.WhenAny(pending, timer).ConfigureAwait(false) == pending)
                    {
                        await wait.CancelAsync().ConfigureAwait(false);
                        break;
                    }

                    await WriteRawAsync(response, ": heartbeat\n\n", cancellationToken).ConfigureAwait(false);
                }

                hasCurrent = await pending.ConfigureAwait(false);
                pending = null;
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            // The client went away; the search is cancelled below.
        }
        catch (EventsEvictedException) when (pending is not null)
        {
            // This listener fell further behind than the replay buffer holds. The stream ends; the client reconnects with
            // its last id and is told whether the gap can still be filled.
            pending = null;
            await WriteRawAsync(http.Response, ": events dropped, reconnect with Last-Event-ID\n\n", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // An async iterator must not be disposed while a MoveNextAsync is still running, so stop the search and let
            // the pending call finish first.
            await stop.CancelAsync().ConfigureAwait(false);
            if (pending is not null)
            {
                try
                {
                    await pending.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: that is how the pending call ends once the search is stopped.
                }
            }
        }
    }

    private static Task WriteEventAsync(HttpResponse response, SequencedUpdate item, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        var update = item.Update;
        var data = JsonSerializer.Serialize(update, update.GetType(), json);
        return WriteRawAsync(response, $"id: {item.Id}\nevent: {update.EventName}\ndata: {data}\n\n", cancellationToken);
    }

    private static async Task WriteRawAsync(HttpResponse response, string text, CancellationToken cancellationToken)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
