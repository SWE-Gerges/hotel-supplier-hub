using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Hub.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hub.Api;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string TenantClaim = "tenant";
}

/// <summary>Looks keys up without branching on how much of the key matched.</summary>
internal sealed class ApiKeyStore
{
    private readonly (byte[] Hash, ApiKeyEntry Entry)[] _entries;

    public ApiKeyStore(IOptions<AuthOptions> options) =>
        _entries = options.Value.Keys.Select(static k => (Hash(k.Key), k)).ToArray();

    public ApiKeyEntry? Find(string providedKey)
    {
        // Hashing first gives both sides the same length, and visiting every entry keeps the work independent of the match.
        var provided = Hash(providedKey);
        ApiKeyEntry? match = null;
        foreach (var (hash, entry) in _entries)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, provided))
            {
                match = entry;
            }
        }

        return match;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeyStore store) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (values.Count != 1 || string.IsNullOrEmpty(values[0]) || store.Find(values[0]!) is not { } entry)
        {
            return Task.FromResult(AuthenticateResult.Fail("The API key is not valid."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, entry.Name),
            new Claim(ClaimTypes.Role, entry.Role),
            new Claim(ApiKeyDefaults.TenantClaim, entry.Tenant),
        ], Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

internal static class PrincipalExtensions
{
    public static SearchOwner ToOwner(this ClaimsPrincipal principal) =>
        new(
            principal.FindFirstValue(ApiKeyDefaults.TenantClaim) ?? throw new InvalidOperationException("The principal has no tenant."),
            principal.Identity?.Name ?? throw new InvalidOperationException("The principal has no name."));
}
