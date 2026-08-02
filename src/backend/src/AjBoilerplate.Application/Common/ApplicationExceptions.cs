namespace AjBoilerplate.Application.Common;

/// <summary>
/// The request could not be completed because it lost a race or contradicts current state — the
/// caller may be able to retry after re-reading. The Api layer's <c>ConflictExceptionHandler</c>
/// maps every subclass to an enveloped 409, so an Application-layer conflict never has to know
/// anything about HTTP.
///
/// Derive from this rather than throwing it directly, so a handler or a test can distinguish the
/// specific conflict when it needs to.
/// </summary>
public abstract class ConflictException : Exception
{
    protected ConflictException(string message) : base(message)
    {
    }
}

/// <summary>
/// The caller is authenticated but not permitted to perform this action. Mapped to an enveloped 403
/// by the Api layer's <c>ForbiddenExceptionHandler</c>.
///
/// This is for the checks a route-level <c>[Authorize]</c> policy cannot make — ownership or
/// record-scope rules that only become decidable after the record has been loaded. Coarse
/// role checks still belong on the endpoint, where they cost nothing.
/// </summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message)
    {
    }
}
