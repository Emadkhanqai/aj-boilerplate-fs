using AjBoilerplate.Contracts.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Fallback handler: an enveloped 500 with no internal detail leaked. The full exception goes to the
/// log — where an operator can correlate it by <c>traceId</c> — and never to the caller: a stack
/// trace, a SQL fragment, or a type name in a response body is an information-disclosure defect.
/// LAST in the handler chain, so it only sees what nothing above it claimed.
/// </summary>
public sealed class UnhandledExceptionHandler : IExceptionHandler
{
    private readonly ILogger<UnhandledExceptionHandler> _logger;

    public UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception processing {Path} (trace {TraceId})",
            httpContext.Request.Path, httpContext.TraceIdentifier);

        var body = ApiResponse.CreateError("An unexpected error occurred.", EnvelopeCodes.InternalError);
        body.TraceId = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken);
        return true;
    }
}
