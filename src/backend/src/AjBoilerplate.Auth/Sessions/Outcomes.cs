namespace AjBoilerplate.Auth.Sessions;

/// <summary>Result of <c>/auth/validate</c>; the HTTP mapping is fixed by the gateway contract.</summary>
public enum ValidationStatus
{
    /// <summary>200 + <c>X-Auth-Jwt</c>.</summary>
    Ok,

    /// <summary>401: the ONLY status nginx may turn into a login redirect.</summary>
    Unauthenticated,

    /// <summary>403 + <c>X-Usp-Access: revoked</c>. Produced ONLY by a definitive no-role, never for a fault.</summary>
    Forbidden,

    /// <summary>503: an infrastructure fault. The session is kept and the user is never sent to sign in for it.</summary>
    Unavailable,
}

/// <param name="Status">The verdict.</param>
/// <param name="Jwt">The JWT to inject; set only on <see cref="ValidationStatus.Ok"/>.</param>
/// <param name="SessionKey">Storage key of the validated session (input to <see cref="Security.PortalSessionId"/>;
/// never logged and never sent); set only on Ok.</param>
/// <param name="SignedInAt">The session's sign-in time; set only on Ok.</param>
public sealed record ValidationResult(
    ValidationStatus Status, string? Jwt = null, string? SessionKey = null, DateTimeOffset? SignedInAt = null)
{
    public static ValidationResult Unauthenticated { get; } = new(ValidationStatus.Unauthenticated);

    public static ValidationResult Forbidden { get; } = new(ValidationStatus.Forbidden);

    public static ValidationResult Unavailable { get; } = new(ValidationStatus.Unavailable);

    public static ValidationResult Ok(string jwt, string sessionKey, DateTimeOffset signedInAt) =>
        new(ValidationStatus.Ok, jwt, sessionKey, signedInAt);
}

/// <param name="AuthorizeUrl">Authorize URL to 302 to.</param>
/// <param name="BindingValue">Value for the short-lived browser-binding cookie.</param>
public sealed record LoginStart(Uri AuthorizeUrl, string BindingValue);

public enum LoginCompletionStatus
{
    /// <summary>Session created; set the cookie and 302 to the return path.</summary>
    SignedIn,

    /// <summary>State missing, expired or replayed, browser binding mismatch, or a dead code: 400.</summary>
    Invalid,

    /// <summary>Keycloak's definitive answer: no role for this app: 403 page, never a redirect.</summary>
    NoAccess,

    /// <summary>The identity provider or Keycloak transiently failed after bounded retries: 503 page, never a redirect.</summary>
    Unavailable,
}

public sealed record LoginCompletion(LoginCompletionStatus Status, string? SessionId = null, string? ReturnPath = null)
{
    public static LoginCompletion Invalid { get; } = new(LoginCompletionStatus.Invalid);

    public static LoginCompletion NoAccess { get; } = new(LoginCompletionStatus.NoAccess);

    public static LoginCompletion Unavailable { get; } = new(LoginCompletionStatus.Unavailable);
}
