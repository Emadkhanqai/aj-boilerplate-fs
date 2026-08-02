using AjBoilerplate.Contracts.Common;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// The single status-code → (<c>message</c>, <c>code</c>) table for enveloped errors.
///
/// It has to be shared because the SAME status can be produced on two different paths and a client
/// must not be able to tell them apart. A 404, for example, arrives either as an unmatched route
/// (handled by <see cref="EnvelopeStatusCodePages"/>) or as a controller's <c>NotFound()</c> — which
/// <c>[ApiController]</c>'s client-error filter has already rewritten into a
/// <c>ProblemDetails</c> result before <see cref="EnvelopeResultFilter"/> ever sees it. Before this
/// table existed the two paths produced <c>NOT_FOUND</c> and <c>REQUEST_FAILED</c> respectively, so
/// a client branching on <c>code</c> broke depending on which one it hit.
/// </summary>
public static class EnvelopeErrors
{
    /// <summary>The stable <c>code</c> slug for <paramref name="statusCode"/>.</summary>
    public static string CodeFor(int statusCode) => Describe(statusCode).Code;

    /// <summary>The human-readable <c>message</c> for <paramref name="statusCode"/>. Deliberately
    /// generic: it names the class of failure without leaking anything about the resource.</summary>
    public static string MessageFor(int statusCode) => Describe(statusCode).Message;

    private static (string Message, string Code) Describe(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => ("One or more validation errors occurred.", EnvelopeCodes.ValidationError),
        StatusCodes.Status401Unauthorized => ("Authentication required.", EnvelopeCodes.Unauthorized),
        StatusCodes.Status403Forbidden => ("You do not have permission to perform this action.", EnvelopeCodes.Forbidden),
        StatusCodes.Status404NotFound => ("The requested resource was not found.", EnvelopeCodes.NotFound),
        StatusCodes.Status405MethodNotAllowed => ("Method not allowed.", EnvelopeCodes.MethodNotAllowed),
        StatusCodes.Status409Conflict => ("The request conflicts with the current state of the resource.", EnvelopeCodes.Conflict),
        StatusCodes.Status415UnsupportedMediaType => ("Unsupported media type.", EnvelopeCodes.UnsupportedMediaType),
        StatusCodes.Status429TooManyRequests => ("Too many requests. Try again shortly.", EnvelopeCodes.TooManyRequests),
        StatusCodes.Status500InternalServerError => ("An unexpected error occurred.", EnvelopeCodes.InternalError),
        StatusCodes.Status503ServiceUnavailable => ("The service is temporarily unavailable.", EnvelopeCodes.ServiceUnavailable),
        _ => ("The request could not be completed.", EnvelopeCodes.RequestFailed),
    };
}
