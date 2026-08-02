namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// Resolves the current request's correlation id, so a domain mutation's audit entries can be tied
/// back to the API call that produced them. Implemented in the Api layer by reading
/// <c>HttpContext.TraceIdentifier</c> — the same value surfaced as <c>traceId</c> on every response
/// envelope — so an audit entry and the API response for the same request always carry the same
/// correlation value.
/// </summary>
public interface ICorrelationContext
{
    string? CorrelationId { get; }
}
