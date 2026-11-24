using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
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
        return app;
    }

    private static async Task<IResult> ValidateAsync(
        HttpContext http,
        SessionValidator validator,
        RolesVersion rolesVersion,
        PortalSessionId sessionIds,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var result = await validator.ValidateAsync(http.Request.Cookies[settings.Cookies.SessionName], cancellationToken);
        if (result.Status != ValidationStatus.Ok)
        {
            return Failure(http, result.Status);
        }

        http.Response.Headers[JwtHeader] = result.Jwt;

        var version = rolesVersion.For(result.Jwt, settings.Portal.ClientId);
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
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var result = await validator.ValidateAsync(http.Request.Cookies[settings.Cookies.SessionName], cancellationToken);
        return result.Status == ValidationStatus.Ok
            ? Results.Json(new RolesVersionResponse(rolesVersion.For(result.Jwt, settings.Portal.ClientId)))
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
        var start = await signIn.BeginAsync(rd, SignInPrompts.Parse(prompt), cancellationToken, loginHint);

        http.Response.Cookies.Append(settings.Cookies.LoginName, start.BindingValue, SessionCookie(settings, settings.Session.LoginStateLifetime));
        return Results.Redirect(start.AuthorizeUrl.AbsoluteUri);
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext http,
        string? code,
        string? state,
        string? error,
        SignInService signIn,
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
            result = await signIn.CompleteAsync(state, code, binding, cancellationToken);
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
                return AuthPages.Render(StatusCodes.Status403Forbidden, "No access", "You are signed in, but you have no role in this application. Contact your administrator.", reference);
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
        if (!IsSameOrigin(http.Request, settings.PublicOrigin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await signIn.SignOutAsync(http.Request.Cookies[settings.Cookies.SessionName], cancellationToken);
        http.Response.Cookies.Delete(settings.Cookies.SessionName, SessionCookie(settings, maxAge: null));

        // The target is one of two constants: no caller-supplied URL is ever echoed.
        http.Response.Headers.Location = http.Request.Query["next"] == NextSignIn ? "/auth/login?prompt=login" : settings.LoggedOutPath;
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

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "The identity provider returned error {Error} to the callback.")]
    private static partial void LogIdentityProviderError(ILogger logger, string error);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Sign-in callback faulted ({ExceptionType}) while completing the sign-in. The browser was shown the fault page; no redirect was issued and no session was ended.")]
    private static partial void LogCallbackFaulted(ILogger logger, Exception exception, string exceptionType);
}
