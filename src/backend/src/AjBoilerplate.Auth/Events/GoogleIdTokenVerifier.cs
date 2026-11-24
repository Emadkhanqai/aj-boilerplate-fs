using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Events;

/// <summary>
/// Check of a Google-signed OIDC token: the Pub/Sub push token on <c>/auth/events</c>, and (opt-in) the caller's ID token on the
/// internal admin endpoints. Where the platform has already verified the token and the invoker binding, this confirms the caller is
/// specifically one of the service accounts we expect and the audience is ours, so a stray invoker binding is not by itself enough.
/// </summary>
/// <remarks>
/// <para>
/// Signature: ALWAYS verified against Google's published keys (RS256, cached for an hour). A token with no signature, an empty
/// signature segment or any other algorithm is refused on both surfaces. There is no configuration that relaxes this.
/// </para>
/// <para>
/// <b>Why an unsigned token is refused.</b> On Cloud Run, when <c>X-Serverless-Authorization</c> is present the platform
/// authenticates on it and forwards <c>Authorization</c> untouched, so any invoker could put a real token there and a hand-built
/// unsigned one claiming to be an allowed caller in <c>Authorization</c>. The service cannot tell "stripped by the platform" from
/// "never had one", so it accepts neither. A caller therefore sends the same Google ID token (audience = this service's base URL)
/// in BOTH headers; a caller that sends only <c>Authorization</c> gets 401 with the reason <c>unsigned token</c>.
/// </para>
/// </remarks>
internal sealed partial class GoogleIdTokenVerifier : IDisposable
{
    private static readonly Uri JwksUri = new("https://www.googleapis.com/oauth2/v3/certs");
    private static readonly TimeSpan JwksLifetime = TimeSpan.FromHours(1);

    /// <summary>An unknown <c>kid</c> forces a refetch (key rotation), but at most this often, so a
    /// caller inventing kids cannot turn every request into a call to Google.</summary>
    private static readonly TimeSpan JwksMinimumRefetchInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];

    public const string HttpClientName = "google-jwks";

    /// <summary>Surface labels. They are the <c>Surface</c> field of every rejection log, so a
    /// query can ask about the admin surface without also matching Pub/Sub pushes.</summary>
    private const string EventsSurface = "/auth/events";
    private const string AdminSurface = "/internal/admin/*";

    private readonly IHttpClientFactory _httpFactory;
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GoogleIdTokenVerifier> _logger;
    private readonly SemaphoreSlim _jwksGate = new(1, 1);
    private JsonDocument? _jwks;
    private DateTimeOffset _jwksFetchedAt;

    public GoogleIdTokenVerifier(IHttpClientFactory httpFactory, IOptions<AuthOptions> options, TimeProvider time, ILogger<GoogleIdTokenVerifier> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>The Pub/Sub push token on <c>/auth/events</c>.</summary>
    public Task<bool> IsPushAuthorizedAsync(string? authorizationHeader, CancellationToken cancellationToken) =>
        IsAuthorizedAsync(
            authorizationHeader,
            EventsSurface,
            _options.Events.Audience,
            [_options.Events.PushServiceAccount],
            cancellationToken);

    /// <summary>The calling service's ID token on <c>/internal/admin/...</c>; false (and nothing logged) when none is configured.</summary>
    public Task<bool> IsAdminCallerAuthorizedAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        if (!_options.Admin.GoogleCallerConfigured)
        {
            return Task.FromResult(false);
        }

        return IsAuthorizedAsync(
            authorizationHeader,
            AdminSurface,
            _options.Admin.Audience,
            _options.Admin.AllowedCallers,
            cancellationToken);
    }

    private async Task<bool> IsAuthorizedAsync(
        string? authorizationHeader,
        string surface,
        string audience,
        IEnumerable<string> allowedEmails,
        CancellationToken cancellationToken)
    {
        const string bearer = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(surface, "missing bearer token");
        }

        var parts = authorizationHeader[bearer.Length..].Trim().Split('.');
        if (parts.Length is < 2 or > 3)
        {
            return Reject(surface, "malformed token");
        }

        JsonElement header, payload;
        try
        {
            header = Decode(parts[0]);
            payload = Decode(parts[1]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return Reject(surface, "undecodable token");
        }

        if (parts.Length == 2 || parts[2].Length == 0)
        {
            // header.payload: the shape Cloud Run forwards when it authenticated on Authorization
            // itself, and the shape a forger sends. Indistinguishable, so refused.
            return Reject(surface, "unsigned token");
        }

        bool signed;
        try
        {
            signed = await VerifySignatureAsync(header, parts[0], parts[1], parts[2], cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            // Google's key set could not be fetched. Fail closed: a call we cannot verify is a call
            // we refuse. Named distinctly so it is not read as a forged or mis-signed token.
            return Reject(surface, "signing keys unavailable");
        }

        if (!signed)
        {
            return Reject(surface, "bad signature");
        }

        return ClaimsAreValid(payload, surface, audience, allowedEmails);
    }

    /// <summary>
    /// The claim checks, each reporting WHICH one failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rejection label that stands for five checks (issuer, audience, caller identity, e-mail verification, expiry) cannot say
    /// which one failed, and an audience mismatch is the commonest. So the claims are checked in order and the failing one is
    /// named; the order is the whole of the logic, only what is SAID about a rejection is richer.
    /// </para>
    /// <para>
    /// <b>What may be logged here.</b> The audience is a service URL and is named on both sides of
    /// a mismatch, because that comparison IS the diagnosis. Nothing else from the payload is
    /// written: not the caller's email (an identity, and the allow-list is already in the manifest),
    /// not expiry timestamps, and under no circumstances the token or its signature.
    /// </para>
    /// </remarks>
    private bool ClaimsAreValid(JsonElement payload, string surface, string audience, IEnumerable<string> allowedEmails)
    {
        if (GetString(payload, "iss") is not { } iss || !Issuers.Contains(iss, StringComparer.Ordinal))
        {
            return Reject(surface, "untrusted issuer");
        }

        var presentedAudience = GetString(payload, "aud");
        if (!string.Equals(presentedAudience, audience, StringComparison.Ordinal))
        {
            LogAudienceMismatch(_logger, surface, audience, presentedAudience ?? "(absent or not a string)");
            return false;
        }

        if (GetString(payload, "email") is not { } email || !allowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
        {
            return Reject(surface, "caller not in the allow-list");
        }

        if (!payload.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
        {
            return Reject(surface, "caller email not verified");
        }

        if (!payload.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds))
        {
            return Reject(surface, "no readable exp claim");
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expSeconds) + ClockSkew <= _time.GetUtcNow())
        {
            return Reject(surface, "token expired");
        }

        return true;
    }

    private async Task<bool> VerifySignatureAsync(JsonElement header, string encodedHeader, string encodedPayload, string encodedSignature, CancellationToken cancellationToken)
    {
        if (GetString(header, "alg") != "RS256" || GetString(header, "kid") is not { } kid)
        {
            return false;
        }

        var key = await FindKeyAsync(kid, refresh: false, cancellationToken)
            ?? await FindKeyAsync(kid, refresh: true, cancellationToken); // key rotation
        if (key is null)
        {
            return false;
        }

        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64Url.DecodeFromChars(GetString(key.Value, "n")),
            Exponent = Base64Url.DecodeFromChars(GetString(key.Value, "e")),
        });

        var signed = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        try
        {
            return rsa.VerifyData(signed, Base64Url.DecodeFromChars(encodedSignature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false; // a signature segment that is not base64url is not a signature
        }
    }

    private async Task<JsonElement?> FindKeyAsync(string kid, bool refresh, CancellationToken cancellationToken)
    {
        await _jwksGate.WaitAsync(cancellationToken);
        try
        {
            var age = _time.GetUtcNow() - _jwksFetchedAt;
            if (_jwks is null || age > JwksLifetime || (refresh && age > JwksMinimumRefetchInterval))
            {
                var json = await _httpFactory.CreateClient(HttpClientName).GetStringAsync(JwksUri, cancellationToken);
                _jwks?.Dispose();
                _jwks = JsonDocument.Parse(json);
                _jwksFetchedAt = _time.GetUtcNow();
            }

            if (_jwks.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in keys.EnumerateArray())
                {
                    if (GetString(key, "kid") == kid)
                    {
                        return key.Clone();
                    }
                }
            }

            return null;
        }
        finally
        {
            _jwksGate.Release();
        }
    }

    public void Dispose()
    {
        _jwksGate.Dispose();
        _jwks?.Dispose();
    }

    private static JsonElement Decode(string segment)
    {
        using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(segment));
        return doc.RootElement.Clone();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private bool Reject(string surface, string reason)
    {
        LogRejected(_logger, surface, reason);
        return false;
    }

    // Both messages are fixed text on purpose: they are what a Cloud Logging query matches on, and
    // "Rejected" plus the Surface and Reason fields is the whole grammar. Reword either and a query
    // written somewhere else stops returning rows without anything failing.
    [LoggerMessage(
        EventId = 9101,
        Level = LogLevel.Warning,
        Message = "Rejected a call to {Surface}: {Reason}.")]
    private static partial void LogRejected(ILogger logger, string surface, string reason);

    [LoggerMessage(
        EventId = 9102,
        Level = LogLevel.Warning,
        Message = "Rejected a call to {Surface}: audience mismatch. This service REQUIRES aud={ExpectedAudience}; the caller presented aud={PresentedAudience}. Both may route to this service and still mismatch -- the comparison is an exact string match.")]
    private static partial void LogAudienceMismatch(ILogger logger, string surface, string expectedAudience, string presentedAudience);
}
