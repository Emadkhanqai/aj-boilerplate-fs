namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Stamps OWASP-aligned response headers on every reply. This is a JSON API that serves no HTML in
/// production, so the CSP is locked down to nothing renderable and framing is denied outright. HSTS
/// itself is applied separately via <c>UseHsts</c> outside Development.
///
/// Registered FIRST in the pipeline (see Program.cs) so the headers reach error and 404 replies too,
/// not just successful ones.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";

        // Swagger UI (Development-only, see Program.cs) serves actual HTML/CSS/JS from this origin —
        // the blanket "renders nothing" CSP below would leave it a blank page in a real browser. A
        // plain curl check will not show this: CSP is browser-enforced, not reflected in the
        // response body or status.
        headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/swagger")
            ? "default-src 'self'; frame-ancestors 'none'"
            : "default-src 'none'; frame-ancestors 'none'";
        headers.Remove("Server");
        return _next(context);
    }
}
