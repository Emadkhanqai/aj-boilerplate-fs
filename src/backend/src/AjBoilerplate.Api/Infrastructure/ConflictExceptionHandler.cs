using AjBoilerplate.Application.Common;
using AjBoilerplate.Contracts.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Maps any Application-layer <see cref="ConflictException"/> to an enveloped 409 — a save that lost
/// an optimistic-concurrency race, a delete blocked by a dependent record, a lost publish race.
///
/// It matches the BASE type on purpose: a new conflict just derives from
/// <see cref="ConflictException"/> and is mapped correctly without this file changing, which is
/// what stops a forgotten <c>switch</c> arm from silently turning a conflict into a 500.
/// </summary>
public sealed class ConflictExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ConflictException conflict)
        {
            return false;
        }

        var body = ApiResponse.CreateError(conflict.Message, EnvelopeCodes.Conflict);
        body.TraceId = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken);
        return true;
    }
}
