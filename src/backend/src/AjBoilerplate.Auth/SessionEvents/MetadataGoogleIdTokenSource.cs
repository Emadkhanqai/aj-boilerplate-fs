using System.Collections.Concurrent;

namespace AjBoilerplate.Auth.SessionEvents;

/// <summary>A Google ID token for a given audience, or null when none can be had.</summary>
internal interface IGoogleIdTokenSource
{
    Task<string?> GetAsync(string audience, CancellationToken cancellationToken);
}

/// <summary>
/// Mints this service's Google ID token from the Cloud Run metadata server, the way the portals'
/// own service-to-service hops do. Cached per audience well inside the token's one-hour life.
/// </summary>
/// <remarks>
/// Off GCP the metadata server does not exist and this returns null; the caller treats that as a
/// retryable failure (<c>id_token_unavailable</c>), so a local run queues events and sends none.
/// The token is never logged, and neither is the response body that carries it.
/// </remarks>
internal sealed partial class MetadataGoogleIdTokenSource : IGoogleIdTokenSource
{
    public const string HttpClientName = "gcp-metadata";

    private const string IdentityUrl =
        "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity?format=full&audience=";

    /// <summary>Google ID tokens live one hour; re-mint well before that.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(45);

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataGoogleIdTokenSource> _logger;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);

    public MetadataGoogleIdTokenSource(IHttpClientFactory http, TimeProvider time, ILogger<MetadataGoogleIdTokenSource> logger)
    {
        _http = http;
        _time = time;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string audience, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(audience, out var cached) && cached.Until > now)
        {
            return cached.Token;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, IdentityUrl + Uri.EscapeDataString(audience));
            request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogMintFailed(_logger, audience, (int)response.StatusCode, "http_status");
                return null;
            }

            var token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (token.Length == 0)
            {
                LogMintFailed(_logger, audience, (int)response.StatusCode, "empty_body");
                return null;
            }

            _cache[audience] = (token, now + CacheFor);
            return token;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogMintFailed(_logger, audience, null, ex is TaskCanceledException ? "timeout" : "transport");
            return null;
        }
    }

    // An audience is a service's own base URL and may be logged (CLAUDE.md's named exception); the
    // token minted for it may not, and is not.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not mint a Google ID token for audience {Audience} from the metadata server: {Reason} (HTTP {Status}).")]
    private static partial void LogMintFailed(ILogger logger, string audience, int? status, string reason);
}
