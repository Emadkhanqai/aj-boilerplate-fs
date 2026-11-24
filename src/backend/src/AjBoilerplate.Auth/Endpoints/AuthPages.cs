using System.Net;

namespace AjBoilerplate.Auth.Endpoints;

/// <summary>The few pages this service renders itself. Deliberately plain: no script, no style, no external resource.</summary>
internal static class AuthPages
{
    public const string ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'";

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
