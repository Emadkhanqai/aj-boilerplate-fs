using AjBoilerplate.Contracts.Common;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Envelopes error responses that never reach an action or an exception handler — an
/// <c>[Authorize]</c> challenge (401/403), an unmatched route (404), a wrong HTTP verb (405), a
/// wrong content type (415), or a rate-limit rejection (429). Wired via
/// <c>app.UseStatusCodePages(...)</c> in Program.cs. Without it, those replies would ship with an
/// empty body and break the "every response is enveloped" contract a client relies on.
///
/// The message and code come from <see cref="EnvelopeErrors"/>, the same table
/// <see cref="EnvelopeResultFilter"/> uses, so the two paths that can produce the same status are
/// indistinguishable to a caller.
/// </summary>
public static class EnvelopeStatusCodePages
{
    public static async Task HandleAsync(HttpContext httpContext)
    {
        var statusCode = httpContext.Response.StatusCode;
        var body = ApiResponse.CreateError(EnvelopeErrors.MessageFor(statusCode), EnvelopeErrors.CodeFor(statusCode));
        body.TraceId = httpContext.TraceIdentifier;

        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsJsonAsync(body);
    }
}
