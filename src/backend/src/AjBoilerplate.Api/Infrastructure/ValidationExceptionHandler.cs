using AjBoilerplate.Contracts.Common;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>Maps FluentValidation failures to an enveloped 400. FIRST in the handler chain
/// (Validation → Conflict → Forbidden → Unhandled) — see Program.cs.</summary>
public sealed class ValidationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
        {
            return false;
        }

        var errors = validationException.Errors
            .Select(e => $"{e.PropertyName}: {e.ErrorMessage}")
            .ToList();

        var body = ApiResponse.CreateError("One or more validation errors occurred.", EnvelopeCodes.ValidationError, errors);
        body.TraceId = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken);
        return true;
    }
}
