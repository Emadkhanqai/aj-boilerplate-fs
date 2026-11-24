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
    /// Fills the key cache once at start-up, before the host accepts traffic, so the first real request
    /// is not the thing that discovers an unreachable JWKS endpoint — on a cloud runtime with no route
    /// out (no NAT, or an egress setting that sends internet traffic into a VPC with no way out) the
    /// fetch hangs until the HttpClient timeout, and a user would meet that as a failed sign-in.
    ///
    /// <para>
    /// A failure NEVER throws: an unreachable identity endpoint is an infrastructure fault, and making
    /// it fatal would turn a degraded service into a crash-looping revision that cannot even serve
    /// <c>/health</c>. The key COUNT in the success line is load-bearing — it is the acceptance test
    /// for the network fix. Never log the keys or the JWKS body.
    /// </para>
    ///
    /// <para>
    /// <c>_lastFetchUtc</c> is stamped only on SUCCESS. Stamping it before the fetch would let a failed
    /// pre-warm suppress the lazy refetch in <see cref="GetSigningKeys"/> for the whole cooldown minute,
    /// contradicting the warning logged here.
    /// </para>
    /// </summary>
    public async Task WarmUpAsync()
    {
        if (_options.JwksUriIsInsecure)
        {
            // Outside Development this is reachable only with Keycloak:AllowInsecureJwksTransport=true.
            _logger.LogWarning(
                "Keycloak JWKS is fetched over plaintext transport ({Scheme}); signing keys can be substituted on the network path. "
                + "Allowed only in Development or with {OptInKey}=true.",
                Uri.TryCreate(_options.JwksUri, UriKind.Absolute, out var jwks) ? jwks.Scheme : "unparsable",
                KeycloakOptions.AllowInsecureJwksTransportKey);
        }

        var fetched = await FetchAsync();

        if (fetched is null)
        {
            _logger.LogWarning(
                "Keycloak JWKS pre-warm at startup failed; the first request needing a signing key will trigger a fetch instead (see GetSigningKeys).");
            return;
        }

        lock (_gate)
        {
            _keysByKid = fetched;
            _lastFetchUtc = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("Keycloak JWKS pre-warmed at startup: {KeyCount} signing key(s) loaded.", fetched.Count);
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
