using Microsoft.Extensions.Options;

namespace Hub.Api;

public sealed class ApiKeyEntry
{
    /// <summary>Identifies the caller. Search runs are owned by tenant and name, so the name must be unique.</summary>
    public string Name { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    /// <summary>"client" may search and manage its own runs; "admin" may additionally use the admin endpoints.</summary>
    public string Role { get; set; } = string.Empty;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public List<ApiKeyEntry> Keys { get; set; } = [];
}

internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();

        if (options.Keys.Count == 0)
        {
            failures.Add("Auth:Keys must define at least one API key.");
        }

        foreach (var entry in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Tenant))
            {
                failures.Add("Every API key needs a Name and a Tenant.");
            }

            if (entry.Key.Length < 16)
            {
                failures.Add($"API key '{entry.Name}' is shorter than 16 characters.");
            }

            if (entry.Role is not (Roles.Client or Roles.Admin))
            {
                failures.Add($"API key '{entry.Name}' has role '{entry.Role}'; use '{Roles.Client}' or '{Roles.Admin}'.");
            }
        }

        if (options.Keys.Select(static k => k.Key).Distinct(StringComparer.Ordinal).Count() != options.Keys.Count)
        {
            failures.Add("API keys must be unique.");
        }

        if (options.Keys.Select(static k => (k.Tenant, k.Name)).Distinct().Count() != options.Keys.Count)
        {
            failures.Add("API key names must be unique within a tenant.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class SseOptions
{
    public const string SectionName = "Hub:Sse";

    /// <summary>How long a stream may stay silent before a comment line keeps the connection alive.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}

internal sealed class SseOptionsValidator : IValidateOptions<SseOptions>
{
    public ValidateOptionsResult Validate(string? name, SseOptions options) =>
        options.HeartbeatInterval > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Hub:Sse:HeartbeatInterval must be positive.");
}

public enum IdentityStoreProvider
{
    InMemory,
    Sqlite,
    Postgres,
}

public sealed class IdentityStoreOptions
{
    public const string SectionName = "Hub:IdentityStore";

    public IdentityStoreProvider Provider { get; set; } = IdentityStoreProvider.InMemory;

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>For PostgreSQL: create the database at startup when the server does not have it (needs the CREATEDB privilege).</summary>
    public bool CreateDatabase { get; set; } = true;
}

internal sealed class IdentityStoreOptionsValidator : IValidateOptions<IdentityStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityStoreOptions options) =>
        options.Provider != IdentityStoreProvider.InMemory && string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail($"Hub:IdentityStore:ConnectionString is required for the {options.Provider} provider.")
            : ValidateOptionsResult.Success;
}

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>The database that holds quotes and bookings (and, later, the city crosswalk).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Hub:Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=hub-state.db";

    /// <summary>The city crosswalk lives in a database of its own, so it can be reset or moved without touching bookings.</summary>
    public string CrosswalkConnectionString { get; set; } = "Data Source=hub-crosswalk.db";

    /// <summary>For PostgreSQL: create the database at startup when the server does not have it (needs the CREATEDB privilege).</summary>
    public bool CreateDatabase { get; set; } = true;
}

internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail("Hub:Database:ConnectionString is required.")
            : ValidateOptionsResult.Success;
}

public sealed class DemoOptions
{
    public const string SectionName = "Hub:Demo";

    /// <summary>Registers every fake-supplier hotel in the identity store at startup (idempotent).</summary>
    public bool SeedIdentities { get; set; }
}

public static class Roles
{
    public const string Client = "client";
    public const string Admin = "admin";
}

public static class Policies
{
    public const string Client = "client";
    public const string Admin = "admin";
}
