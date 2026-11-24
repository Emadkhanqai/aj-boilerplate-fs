using System.Net;

namespace AjBoilerplate.Auth.Endpoints;

/// <summary>The few pages this service renders itself. Deliberately plain: no script, no style, no external resource.</summary>
internal static class AuthPages
{
    public const string ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    /// <summary>
    /// The sign-out the "Back to sign-in" button posts to: it ends THIS session, then goes straight into sign-in with
    /// <c>prompt=login</c>, so the person can pick another account.
    /// </summary>
    public const string SignOutThenSignInPath = "/auth/logout?next=" + AuthEndpoints.NextSignIn;

    /// <summary>
    /// Only on a page carrying the sign-out form. These pages arrive with <c>Referrer-Policy: no-referrer</c> (the callback URL carries
    /// the authorization code), and under that policy a browser sends <c>Origin: null</c> on a form POST, which the logout's same-origin
    /// check refuses with 403. <c>strict-origin</c> sends the real <c>Origin</c> and at most the bare origin as Referer, never the path
    /// or query, so the code still never leaves in a Referer. The check itself is unchanged.
    /// </summary>
    private const string SignOutReferrerMeta = "<meta name=\"referrer\" content=\"strict-origin\">";

    /// <summary>
    /// The 403 "No access" page: signed in, but no role in this app. Names the app (configuration, HTML-encoded) and offers one button
    /// that ends this app's session and goes to sign-in. Nothing from the request is echoed but the correlation id.
    /// </summary>
    public static IResult NoAccess(string? appName, string? reference)
    {
        var name = string.IsNullOrWhiteSpace(appName) ? "this application" : appName;
        var referenceLine = string.IsNullOrEmpty(reference) ? string.Empty : $"<p><small>Reference: {WebUtility.HtmlEncode(reference)}</small></p>";
        var html =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + SignOutReferrerMeta
            + "<title>No access</title></head><body><main>"
            + $"<h1>You have no access to {WebUtility.HtmlEncode(name)}</h1>"
            + "<p>You are signed in, but you have no role in this application. Contact your administrator.</p>"
            + $"<form method=\"post\" action=\"{SignOutThenSignInPath}\"><button type=\"submit\">Back to sign-in</button></form>"
            + $"<p><small>This signs you out of {WebUtility.HtmlEncode(name)} only, and you can then sign in with a different account.</small></p>"
            + $"{referenceLine}</main></body></html>";
        return Results.Content(html, "text/html; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);
    }

    public static IResult Render(int status, string title, string message, string? reference)
    {
        var referenceLine = string.IsNullOrEmpty(reference) ? string.Empty : $"<p><small>Reference: {WebUtility.HtmlEncode(reference)}</small></p>";
        var html =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + $"<title>{WebUtility.HtmlEncode(title)}</title></head><body><main><h1>{WebUtility.HtmlEncode(title)}</h1>"
            + $"<p>{WebUtility.HtmlEncode(message)}</p>{referenceLine}</main></body></html>";
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }
}
