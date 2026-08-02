using AjBoilerplate.Application.Abstractions;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Resolves the current request's correlation id from <c>HttpContext.TraceIdentifier</c> — the same
/// value surfaced as <c>traceId</c> on every response envelope — so an audit entry, an outbox row,
/// and the API response for the same request always carry the same value.
/// </summary>
public sealed class HttpCorrelationContext : ICorrelationContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCorrelationContext(IHttpContextAccessor httpContextAccessor) => _httpContextAccessor = httpContextAccessor;

    public string? CorrelationId => _httpContextAccessor.HttpContext?.TraceIdentifier;
}
