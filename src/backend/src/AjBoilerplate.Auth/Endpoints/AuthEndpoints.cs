using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Events;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Observability;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Endpoints;

/// <summary>The browser-facing and nginx-facing endpoints. The wire contract is docs/auth/gateway-contract.md.</summary>
internal static partial class AuthEndpoints
{
    public const string JwtHeader = "X-Auth-Jwt";
    public const string RolesVersionHeader = "X-Usp-Roles-Version";
    public const string SessionIdHeader = "X-Usp-Session-Id";
    public const string SessionStartedAtHeader = "X-Usp-Session-Started-At";
    public const string AccessHeader = "X-Usp-Access";
    public const string AccessRevoked = "revoked";
    public const string FailureHeader = "X-Usp-Failure";
    public const string FailureAuth = "auth";
    public const string NextSignIn = "sign-in";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").AddEndpointFilter(async (context, next) =>
        {
            ApplyResponseHeaders(context.HttpContext.Response);
            return await next(context);
        });

        auth.MapGet("/validate", ValidateAsync);
        auth.MapGet("/roles-version", RolesVersionAsync);
        auth.MapGet("/login", LoginAsync);
        auth.MapGet("/callback", CallbackAsync);
        auth.MapPost("/logout", LogoutAsync);
        auth.MapPost("/events", EventsAsync);
        auth.MapGet("/handoff", HandoffAsync);
        auth.MapPost("/handoff/redeem", RedeemHandoffAsync);
        auth.MapGet("/handoff/redeem", StaleRedeem);
        auth.MapGet("/no-access", NoAccessAfterRedeem);
        return app;
    }

    private static async Task<IResult> ValidateAsync(
        HttpContext http,
        SessionValidator validator,
        RolesVersion rolesVersion,
        PortalSessionId sessionIds,
        PortalRegistry portals,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var host = PortalHost(http, settings);
        var result = await validator.ValidateAsync(http.Request.Cookies[settings.Cookies.SessionName], host, cancellationToken);
        if (result.Status != ValidationStatus.Ok)
        {
            return Failure(http, result.Status);
        }

        http.Response.Headers[JwtHeader] = result.Jwt;

        var portal = portals.Resolve(host);
        var version = portal is null ? null : rolesVersion.For(result.Jwt, portal.ClientId);
        if (version is not null)
        {
            http.Response.Headers[RolesVersionHeader] = version;
        }

        http.Response.Headers[SessionIdHeader] = sessionIds.For(result.SessionKey!);
        http.Response.Headers[SessionStartedAtHeader] = PortalSessionId.FormatStartedAt(result.SignedInAt!.Value);
        return Results.Ok();
    }

    private static async Task<IResult> RolesVersionAsync(
        HttpContext http,
        SessionValidator validator,
        RolesVersion rolesVersion,
        PortalRegistry portals,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var host = PortalHost(http, settings);
        var result = await validator.ValidateAsync(http.Request.Cookies[settings.Cookies.SessionName], host, cancellationToken);
        var portal = portals.Resolve(host);
        return result.Status == ValidationStatus.Ok
            ? Results.Json(new RolesVersionResponse(portal is null ? null : rolesVersion.For(result.Jwt, portal.ClientId)))
            : Failure(http, result.Status);
    }

    private sealed record RolesVersionResponse(string? RolesVersion);

    private static IResult Failure(HttpContext http, ValidationStatus status)
    {
        switch (status)
        {
            case ValidationStatus.Forbidden:
                http.Response.Headers[AccessHeader] = AccessRevoked;
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            case ValidationStatus.Unavailable:
                http.Response.Headers[FailureHeader] = FailureAuth;
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            default:
                return Results.Unauthorized();
        }
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        string? rd,
        string? prompt,
        [FromQuery(Name = "login_hint")] string? loginHint,
        SignInService signIn,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var host = PortalHost(http, settings);
        if (signIn.Resolve(host) is null)
        {
            return Results.NotFound();
        }

        var start = await signIn.BeginAsync(rd, SignInPrompts.Parse(prompt), cancellationToken, loginHint, host);

        http.Response.Cookies.Append(settings.Cookies.LoginName, start.BindingValue, SessionCookie(settings, settings.Session.LoginStateLifetime));
        return Results.Redirect(start.AuthorizeUrl.AbsoluteUri);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext http,
        string? code,
        string? state,
        string? error,
        SignInService signIn,
        PortalRegistry portals,
        IOptions<AuthOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var logger = loggers.CreateLogger(typeof(AuthEndpoints).FullName!);
        var binding = http.Request.Cookies[settings.Cookies.LoginName];
        http.Response.Cookies.Delete(settings.Cookies.LoginName, SessionCookie(settings, maxAge: null));
        var reference = http.TraceIdentifier;

        if (!string.IsNullOrEmpty(error))
        {
            // The error CODE only: error_description is user-supplied text and never logged.
            LogIdentityProviderError(logger, Truncate(error, 64));
            return error == "access_denied"
                ? AuthPages.Render(StatusCodes.Status403Forbidden, "Access denied", "Your account was not allowed to sign in. Contact your administrator.", reference)
                : AuthPages.Render(StatusCodes.Status400BadRequest, "Sign-in failed", "Sign-in could not be completed. Go back and try again.", reference);
        }

        LoginCompletion result;
        try
        {
            result = await signIn.CompleteAsync(state, code, binding, cancellationToken, PortalHost(http, settings));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A fault here (SQL, Redis, a malformed token) is not a verdict about the user: a page, never a redirect.
            LogCallbackFaulted(logger, ex, ex.GetType().FullName!);
            return AuthPages.Render(StatusCodes.Status503ServiceUnavailable, "Sign-in temporarily unavailable", "Please try again in a few minutes.", reference);
        }

        switch (result.Status)
        {
            case LoginCompletionStatus.SignedIn:
                http.Response.Cookies.Append(settings.Cookies.SessionName, result.SessionId!, SessionCookie(settings, maxAge: null));
                return Results.Redirect(result.ReturnPath!);
            case LoginCompletionStatus.NoAccess:
                return NoAccessPage(http, portals, settings, reference);
            case LoginCompletionStatus.Unavailable:
                return AuthPages.Render(StatusCodes.Status503ServiceUnavailable, "Sign-in temporarily unavailable", "Please try again in a few minutes.", reference);
            default:
                return AuthPages.Render(StatusCodes.Status400BadRequest, "Sign-in expired", "This sign-in link has expired or was already used. Go back and try again.", reference);
        }
    }

    // POST + same-origin check + 303: a GET sign-out is CSRF-able by an <img> on any site.
    private static async Task<IResult> LogoutAsync(
        HttpContext http, SignInService signIn, IOptions<AuthOptions> options, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var portal = signIn.Resolve(PortalHost(http, settings));
        if (portal is null)
        {
            return Results.NotFound();
        }

        if (!IsSameOrigin(http.Request, portal.Origin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await signIn.SignOutAsync(http.Request.Cookies[settings.Cookies.SessionName], cancellationToken);
        http.Response.Cookies.Delete(settings.Cookies.SessionName, SessionCookie(settings, maxAge: null));

        // The target is one of two constants: no caller-supplied URL is ever echoed.
        http.Response.Headers.Location = http.Request.Query["next"] == NextSignIn
            ? "/auth/login?prompt=login"
            : string.IsNullOrEmpty(portal.LoggedOutPath) ? settings.LoggedOutPath : portal.LoggedOutPath;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    internal static bool IsSameOrigin(HttpRequest request, string publicOrigin)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            return string.Equals(origin, publicOrigin, StringComparison.OrdinalIgnoreCase);
        }

        // Some browsers omit Origin on same-origin form posts; Fetch Metadata is then authoritative.
        return string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "same-origin", StringComparison.Ordinal);
    }

    private static Microsoft.AspNetCore.Http.CookieOptions SessionCookie(AuthOptions options, TimeSpan? maxAge) => new()
    {
        Secure = options.Cookies.Secure,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = maxAge,
        IsEssential = true,
    };

    internal static void ApplyResponseHeaders(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.ContentSecurityPolicy = AuthPages.ContentSecurityPolicy;
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    // ---- Role-change push (Pub/Sub) -----------------------------------------------------------

    /// <summary>
    /// <c>POST /auth/events</c>: a Google-signed push saying a Keycloak user changed. Disabled (404) until
    /// <c>Auth:Events:Audience</c> and <c>PushServiceAccount</c> are both set. The token is always signature-verified.
    /// </summary>
    private static async Task<IResult> EventsAsync(
        HttpContext http,
        GoogleIdTokenVerifier verifier,
        ForceRefreshHandler handler,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Events.IsEnabled)
        {
            return Results.NotFound();
        }

        if (!await verifier.IsPushAuthorizedAsync(http.Request.Headers.Authorization, cancellationToken))
        {
            return Results.Unauthorized();
        }

        JsonElement envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Unparseable forever: acknowledge, otherwise Pub/Sub redelivers it until retention ends.
            return Results.NoContent();
        }

        var subject = UserChangeMessage.TryGetSubject(envelope);
        if (subject is not null)
        {
            // A Redis failure throws, so 500, so Pub/Sub redelivers. The handler is idempotent.
            await handler.HandleAsync(subject, cancellationToken);
        }

        return Results.NoContent();
    }

    // ---- Portal hand-off (docs/auth/handoff.md) -----------------------------------------------

    /// <summary>
    /// The one script of the hand-off page: submits the form, or goes Back when the page was reached by Back/Forward (the ticket is
    /// spent, and re-submitting it would only fall back to sign-in). Allowed by CSP hash, so the page needs no <c>unsafe-inline</c>.
    /// </summary>
    internal const string HandoffScript =
        "(function(){var n=performance.getEntriesByType('navigation')[0];"
        + "if(n&&n.type==='back_forward'){history.back();return;}"
        + "document.getElementById('handoff').submit();})();";

    private static readonly string HandoffScriptHash =
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(HandoffScript))) + "'";

    /// <summary>
    /// <c>GET /auth/handoff?to=&lt;client id&gt;&amp;rd=&lt;path&gt;</c> on the SOURCE portal. <c>to</c> is a KEY into the configured
    /// portals, never a URL. With a single portal there is nobody to hand off to: any <c>to</c> but this portal is a 404 page.
    /// </summary>
    private static async Task<IResult> HandoffAsync(
        HttpContext http, string? to, string? rd, HandoffService handoff, IOptions<AuthOptions> options, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var start = await handoff.BeginAsync(
            PortalHost(http, settings),
            to,
            rd,
            http.Request.Cookies[settings.Cookies.SessionName],
            http.Request.Headers["Sec-Fetch-Site"].ToString(),
            cancellationToken);

        switch (start.Status)
        {
            case HandoffStartStatus.UnknownSource:
                return Results.NotFound();
            case HandoffStartStatus.UnknownTarget:
                return AuthPages.Render(StatusCodes.Status404NotFound, "Unknown application", "That link is not set up.", http.TraceIdentifier);
            case HandoffStartStatus.SamePortal:
                return SeeOther(http, start.ReturnPath);
            case HandoffStartStatus.Fallback:
                return SeeOther(http, $"{start.TargetOrigin}/auth/login?rd={Uri.EscapeDataString(start.ReturnPath)}");
            default:
                return HandoffPage(http, start.TargetOrigin!, start.Ticket!);
        }
    }

    private static IResult HandoffPage(HttpContext http, string targetOrigin, string ticket)
    {
        http.Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src {HandoffScriptHash}; base-uri 'none'; frame-ancestors 'none'";

        // The ticket travels in a POST BODY, never a URL: a URL lands in request logs, the gateway's error log and browser history.
        // `strict-origin` so the redeem POST carries the real Origin the target checks, under the page's own no-referrer policy.
        var action = WebUtility.HtmlEncode($"{targetOrigin}{HandoffService.RedeemPath}");
        return Results.Content(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"referrer\" content=\"strict-origin\"><title>Opening the application</title></head><body>"
            + $"<form id=\"handoff\" method=\"post\" action=\"{action}\">"
            + $"<input type=\"hidden\" name=\"ticket\" value=\"{WebUtility.HtmlEncode(ticket)}\">"
            + "<noscript><p>Continue to the application.</p><button type=\"submit\">Continue</button></noscript>"
            + $"</form><script>{HandoffScript}</script></body></html>",
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary><c>POST /auth/handoff/redeem</c> on the TARGET portal. Every outcome is a <c>303</c>, never a page rendered on the POST URL.</summary>
    private static async Task<IResult> RedeemHandoffAsync(
        HttpContext http, HandoffService handoff, IOptions<AuthOptions> options, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        HandoffRedemption result;
        try
        {
            // Read by hand rather than bound with [FromForm], which would attach antiforgery metadata this service has no middleware
            // for. The CSRF control here is the Origin check inside RedeemAsync.
            string? ticket = null;
            if (http.Request.HasFormContentType)
            {
                var form = await http.Request.ReadFormAsync(cancellationToken);
                ticket = form["ticket"].ToString();
            }

            result = await handoff.RedeemAsync(
                PortalHost(http, settings),
                ticket,
                http.Request.Headers.Origin.ToString(),
                http.Request.Cookies[settings.Cookies.SessionName],
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A fault here is NOT a dead end: the ordinary sign-in is a different path that does not come back through this one, so
            // falling back cannot loop. Nothing from the request is logged: the body carries the ticket.
            LogHandoffFaulted(loggers.CreateLogger(typeof(AuthEndpoints).FullName!), ex, ex.GetType().FullName!);
            result = new HandoffRedemption(HandoffRedemptionStatus.Fallback);
        }

        switch (result.Status)
        {
            case HandoffRedemptionStatus.UnknownHost:
                return Results.NotFound();
            case HandoffRedemptionStatus.SignedIn:
                http.Response.Cookies.Append(settings.Cookies.SessionName, result.SessionId!, SessionCookie(settings, maxAge: null));
                return SeeOther(http, result.ReturnPath);
            case HandoffRedemptionStatus.Reused:
                return SeeOther(http, result.ReturnPath);
            case HandoffRedemptionStatus.NoAccess:
                // POST-redirect-GET, like every other outcome here: rendered in place, the page would sit on the POST-only URL and a
                // refresh would answer 405. The 303 goes to a page, NOT to /auth/login, because a login redirect is the identity
                // provider loop a definitive no-role must not enter. The ticket is already spent and is in no URL.
                http.Response.Cookies.Append(settings.Cookies.NoAccessName, "1", SessionCookie(settings, NoAccessMarkerLifetime));
                return SeeOther(http, NoAccessPath);
            default:
                return SeeOther(http, $"/auth/login?rd={Uri.EscapeDataString(result.ReturnPath)}");
        }
    }

    private static readonly TimeSpan NoAccessMarkerLifetime = TimeSpan.FromMinutes(1);

    /// <summary>A GET of the redeem URL (a refresh, Back/Forward) redeems nothing: it goes to the ordinary entry point.</summary>
    private static IResult StaleRedeem(HttpContext http) => SeeOther(http, "/");

    /// <summary>
    /// <c>GET /auth/no-access</c>: the "No access" page, shown ONCE after a hand-off redeem that found no role, or after a gateway that
    /// set the marker cookie (see docs/auth/gateway-contract.md). Without the marker it goes to <c>/</c>, so a later visit by someone
    /// who has since been granted a role is never shown a stale refusal.
    /// </summary>
    private static IResult NoAccessAfterRedeem(HttpContext http, PortalRegistry portals, IOptions<AuthOptions> options)
    {
        var settings = options.Value;
        if (portals.Resolve(PortalHost(http, settings)) is null)
        {
            return Results.NotFound();
        }

        if (!http.Request.Cookies.ContainsKey(settings.Cookies.NoAccessName))
        {
            return SeeOther(http, "/");
        }

        http.Response.Cookies.Delete(settings.Cookies.NoAccessName, SessionCookie(settings, maxAge: null));
        return NoAccessPage(http, portals, settings, http.TraceIdentifier);
    }

    private static IResult NoAccessPage(HttpContext http, PortalRegistry portals, AuthOptions settings, string? reference) =>
        AuthPages.NoAccess(portals.Resolve(PortalHost(http, settings))?.DisplayName, reference);

    private static IResult SeeOther(HttpContext http, string location)
    {
        http.Response.Headers.Location = location;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>
    /// The browser-facing host, for choosing the portal. <c>null</c> in a single-portal installation: nothing is then read from the
    /// request, so a missing or odd forwarded-host header cannot change which portal answers.
    /// </summary>
    internal static string? PortalHost(HttpContext http, AuthOptions options)
    {
        if (options.Portals.Count == 0)
        {
            return null;
        }

        var forwarded = http.Request.Headers[options.ForwardedHostHeader].ToString();
        return string.IsNullOrWhiteSpace(forwarded) ? http.Request.Host.Host : forwarded;
    }

    /// <summary>Where a hand-off redeem that found no role sends the browser, by <c>303</c>.</summary>
    public const string NoAccessPath = "/auth/no-access";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Portal hand-off redeem faulted ({ExceptionType}); the browser was sent to the ordinary sign-in and no session was ended.")]
    private static partial void LogHandoffFaulted(ILogger logger, Exception exception, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The identity provider returned error {Error} to the callback.")]
    private static partial void LogIdentityProviderError(ILogger logger, string error);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Sign-in callback faulted ({ExceptionType}) while completing the sign-in. The browser was shown the fault page; no redirect was issued and no session was ended.")]
    private static partial void LogCallbackFaulted(ILogger logger, Exception exception, string exceptionType);
}
