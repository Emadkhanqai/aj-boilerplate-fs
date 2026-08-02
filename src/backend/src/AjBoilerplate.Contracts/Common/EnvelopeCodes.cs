namespace AjBoilerplate.Contracts.Common;

/// <summary>Stable <c>code</c> slugs used across every enveloped error response. A client branches
/// on these, never on the human-readable <c>message</c>.</summary>
public static class EnvelopeCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string Conflict = "CONFLICT";
    public const string InternalError = "INTERNAL_ERROR";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string RequestFailed = "REQUEST_FAILED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
}
