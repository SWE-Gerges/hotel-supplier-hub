using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hub.Tests;

/// <summary>The real API host in memory, with fake suppliers, a short SSE heartbeat and test-only API keys.</summary>
public class HubApiFactory : WebApplicationFactory<Program>
{
    public const string ClientKey = "test-client-key-0000-not-a-secret";
    public const string AdminKey = "test-admin-key-0000-not-a-secret";
    public const string OtherTenantKey = "test-other-key-00000-not-a-secret";
    public const string LimitedKey = "test-limited-key-000-not-a-secret";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hub-api-state-{Guid.NewGuid():N}.db");

    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Hub:Database:ConnectionString"] = $"Data Source={_databasePath};Pooling=False",
        ["Hub:Database:CrosswalkConnectionString"] = $"Data Source={_databasePath}.crosswalk;Pooling=False",
        ["Hub:Bookings:RunBackgroundServices"] = "false",
        ["Auth:Keys:0:Name"] = "test-client",
        ["Auth:Keys:0:Key"] = ClientKey,
        ["Auth:Keys:0:Tenant"] = "tenant-demo",
        ["Auth:Keys:0:Role"] = "client",
        ["Auth:Keys:1:Name"] = "test-admin",
        ["Auth:Keys:1:Key"] = AdminKey,
        ["Auth:Keys:1:Tenant"] = "tenant-demo",
        ["Auth:Keys:1:Role"] = "admin",
        ["Auth:Keys:2:Name"] = "test-client",
        ["Auth:Keys:2:Key"] = OtherTenantKey,
        ["Auth:Keys:2:Tenant"] = "tenant-limited",
        ["Auth:Keys:2:Role"] = "client",
        ["Auth:Keys:3:Name"] = "test-other-client",
        ["Auth:Keys:3:Key"] = LimitedKey,
        ["Auth:Keys:3:Tenant"] = "tenant-demo",
        ["Auth:Keys:3:Role"] = "client",
        ["Hub:Sse:HeartbeatInterval"] = "00:00:00.100",
        ["Hub:Resilience:Defaults:RetryBaseDelay"] = "00:00:00.010",
        ["Hub:Demo:SeedIdentities"] = "true",
        ["Hub:Streaming:DisconnectGrace"] = "00:00:00",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting, unlike ConfigureAppConfiguration, is visible to Program.cs while it registers services.
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(ConfigureServicesForTest);
    }

    /// <summary>Everything the host logged, so tests can assert that a scenario produced no error-level lines.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    protected virtual void ConfigureServicesForTest(IServiceCollection services)
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_databasePath);
            File.Delete(_databasePath + ".crosswalk");
        }
    }

    public HttpClient ClientFor(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    public static object SearchBody(
        string destination = "city-aldermoor",
        string[]? suppliers = null,
        int? waitMs = null,
        string? searchId = null,
        string? replaces = null,
        string currency = "EUR",
        object? options = null) => new
    {
        destination,
        checkIn = "2027-03-01",
        checkOut = "2027-03-04",
        rooms = new[] { new { adults = 2 } },
        nationality = "XA",
        currency,
        suppliers,
        waitMs,
        searchId,
        replaces,
        options,
    };

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("The response had no JSON body.");

    public static Task<HttpResponseMessage> SetFaultsAsync(HttpClient admin, string supplier, object profile) =>
        admin.PutAsJsonAsync($"/api/v1/admin/suppliers/{supplier}/faults", profile);

    public static Task<HttpResponseMessage> ResetFaultsAsync(HttpClient admin, string supplier) =>
        SetFaultsAsync(admin, supplier, new { });

    public static Task<HttpResponseMessage> OpenStreamAsync(HttpClient client, object body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/searches/stream") { Content = JsonContent.Create(body) };
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}

public sealed record SseMessage(string? Id, string? Event, string? Data, string? Comment)
{
    public JsonNode Json => JsonNode.Parse(Data ?? throw new InvalidOperationException("The message has no data."))!;
}

internal static class Sse
{
    public static async IAsyncEnumerable<SseMessage> ReadAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? id = null;
        string? name = null;
        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (id is not null || name is not null || data is not null)
                {
                    yield return new SseMessage(id, name, data, null);
                }

                id = name = data = null;
            }
            else if (line[0] == ':')
            {
                yield return new SseMessage(null, null, null, line[1..].Trim());
            }
            else
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var field = line[..colon];
                var value = line[(colon + 1)..].TrimStart(' ');
                switch (field)
                {
                    case "id": id = value; break;
                    case "event": name = value; break;
                    case "data": data = value; break;
                }
            }
        }
    }

    public static async Task<List<SseMessage>> ReadAllAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var all = new List<SseMessage>();
        await foreach (var message in ReadAsync(response, cancellationToken))
        {
            all.Add(message);
        }

        return all;
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, System.Collections.Concurrent.ConcurrentQueue<(LogLevel, string, string)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? string.Empty : " " + exception.GetType().Name)));
    }
}
