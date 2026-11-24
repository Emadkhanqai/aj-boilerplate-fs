using System.Globalization;
using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.AuthenticationHistory;
using Microsoft.Extensions.Caching.Memory;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Records each sign-in once, from the gateway's <c>X-Usp-Session-Id</c> and
/// <c>X-Usp-Session-Started-At</c> headers (<c>docs/auth/gateway-contract.md</c>). Runs after
/// authorization, so only a request whose JWT has been verified and which holds a role gets here; the
/// sign-in is attributed to that JWT's subject.
///
/// <para>
/// Trusted for what the gateway guarantees and no more: the headers are not signed. The API is
/// reachable only through the gateway, which overwrites both on every <c>/api/</c> request. A missing
/// or malformed header records nothing — never a fallback to <c>jti</c>, <c>iat</c> or the request
/// time, which is the one-row-per-token-renewal duplication this exists to remove.
/// </para>
///
/// <para>
/// A per-id memory cache keeps the database out of every request after the first; the unique index on
/// the session id still decides when two first requests race, or two instances see the same session.
/// A failure is logged and swallowed: recording a sign-in must never fail a request. The session id is
/// not a credential and may be logged; no token or header value other than it ever is.
/// </para>
/// </summary>
public sealed class SignInRecordingMiddleware
{
    public const string SessionIdHeader = "X-Usp-Session-Id";
    public const string SessionStartedAtHeader = "X-Usp-Session-Started-At";

    // Longer than the 12 h absolute session lifetime in the gateway contract, so a session never
    // outlives its own memo and gets looked up again.
    private static readonly TimeSpan Remember = TimeSpan.FromHours(13);

    private readonly RequestDelegate _next;

    public SignInRecordingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context, IMemoryCache cache, ISignInRecorder recorder, ILogger<SignInRecordingMiddleware> logger)
    {
        await TryRecordAsync(context, cache, recorder, logger);
        await _next(context);
    }

    private static async Task TryRecordAsync(
        HttpContext context, IMemoryCache cache, ISignInRecorder recorder, ILogger logger)
    {
        var request = context.Request;
        // A machine caller (the Auth service's session-ended events) is not a person signing in: it
        // carries no session headers and its principal is a fixed service identity.
        if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/api/integration", StringComparison.OrdinalIgnoreCase)
            || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var sessionId = request.Headers[SessionIdHeader].ToString();
        if (sessionId.Length == 0
            || !DateTimeOffset.TryParse(
                request.Headers[SessionStartedAtHeader].ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var startedAt))
        {
            return;
        }

        var cacheKey = "signin-recorded:" + sessionId;
        if (cache.TryGetValue(cacheKey, out _))
        {
            return;
        }

        var subject = ActorIdClaim.Find(context.User);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        try
        {
            var displayName = context.User.FindFirst("name")?.Value ?? context.User.FindFirst("preferred_username")?.Value;
            var recorded = await recorder.RecordAsync(subject, displayName, sessionId, startedAt, context.RequestAborted);
            cache.Set(cacheKey, true, Remember);
            if (recorded)
            {
                logger.LogDebug("Recorded a sign-in for session {SessionId}.", sessionId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record a sign-in; the request proceeds unaffected.");
        }
    }
}
