using AjBoilerplate.Application.Common;
using AjBoilerplate.Contracts.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Maps a service-layer authorization denial (<see cref="ForbiddenException"/>) to an enveloped 403
/// — the same shape <see cref="EnvelopeStatusCodePages"/> produces for a coarse <c>[Authorize]</c>
/// policy failure, but reachable from inside an action after the record has already been loaded.
/// Ownership and record-scope checks need the loaded aggregate, which a route-level policy attribute
/// cannot see.
/// </summary>
public sealed class ForbiddenExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ForbiddenException forbidden)
        {
            return false;
        }

        var body = ApiResponse.CreateError(forbidden.Message, EnvelopeCodes.Forbidden);
        body.TraceId = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken);
        return true;
    }
}
