using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Sources JWT signing keys directly from <see cref="KeycloakOptions.JwksUri"/> instead of through
/// OIDC discovery.
///
/// This exists because discovery is not always usable: when Keycloak sits behind a gateway, the
/// discovery document loads fine but its own <c>jwks_uri</c> field advertises the realm's internal
/// or externally-firewalled address, and the JwtBearer handler then blocks for minutes following it
/// before giving up — every request 401ing meanwhile. Fetching an explicitly configured JWKS URL,
/// and never touching discovery at all, sidesteps that entirely.
///
/// Wired as <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/>, a synchronous
/// delegate: a cache hit returns immediately, and a cache miss (first use, or an unknown key id
/// after a realm key rotation) blocks the calling thread just long enough to refetch, guarded by a
/// lock and a short HttpClient timeout so a dead endpoint fails fast instead of hanging. A fetch
/// failure keeps whatever keys were already cached — unknown kids still fail closed, but
/// known-good keys survive a transient outage — and a cooldown stops a bogus-kid token from
/// hammering the endpoint on every request.
/// </summary>
public sealed class KeycloakSigningKeyProvider
{
    private static readonly TimeSpan RefetchCooldown = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly KeycloakOptions _options;
    private readonly ILogger<KeycloakSigningKeyProvider> _logger;
    private readonly Lock _gate = new();

    private IReadOnlyDictionary<string, SecurityKey> _keysByKid = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
    private DateTimeOffset _lastFetchUtc = DateTimeOffset.MinValue;

    public KeycloakSigningKeyProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<KeycloakOptions> options,
        ILogger<KeycloakSigningKeyProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns the signing key(s) matching <paramref name="kid"/>. Shaped to be used directly as an
    /// <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/> delegate body.
    /// </summary>
    public IEnumerable<SecurityKey> GetSigningKeys(string? kid)
    {
        if (kid is null)
        {
            return [];
        }

        var cached = _keysByKid;
        if (cached.TryGetValue(kid, out var key))
        {
            return [key];
        }

        lock (_gate)
        {
            // Re-read inside the lock: another thread may have refreshed the cache while this one
            // was waiting to acquire it.
            cached = _keysByKid;
            if (cached.TryGetValue(kid, out var refreshedKey))
            {
                return [refreshedKey];
            }

            if (DateTimeOffset.UtcNow - _lastFetchUtc < RefetchCooldown)
            {
                // Anti-hammering: a still-unknown kid within the cooldown window fails closed
                // immediately instead of re-hitting the endpoint on every request presenting it.
                return [];
            }

            _lastFetchUtc = DateTimeOffset.UtcNow;

            // A failed fetch (null) keeps the previously-known keys instead of wiping them: an
            // unknown kid still fails closed below, but tokens signed with an already-cached key
            // must not start 401ing because a refetch hit a transient outage — that would let a
            // single bogus-kid token during a blip knock out every caller for the whole cooldown.
            var fetched = FetchAsync().GetAwaiter().GetResult();
            if (fetched is not null)
            {
                _keysByKid = fetched;
            }

            return _keysByKid.TryGetValue(kid, out var fetchedKey) ? [fetchedKey] : [];
        }
    }

    private async Task<IReadOnlyDictionary<string, SecurityKey>?> FetchAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(KeycloakSigningKeyProvider));
            using var response = await client.GetAsync(_options.JwksUri);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Keycloak JWKS fetch returned HTTP {StatusCode}; keeping previously-cached keys until the next retry.",
                    (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var jwks = new JsonWebKeySet(json);
            return jwks.Keys
                .Where(k => !string.IsNullOrEmpty(k.Kid))
                .ToDictionary(k => k.Kid, k => (SecurityKey)k, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Deliberately broad: an unreachable host, a connection reset, a timed-out request, or a
            // malformed JWKS body must all fail closed the same way, and no unexpected exception type
            // may escape from inside the synchronous resolver delegate.
            _logger.LogWarning(ex, "Keycloak JWKS fetch failed; keeping previously-cached keys until the next retry.");
            return null;
        }
    }
}
