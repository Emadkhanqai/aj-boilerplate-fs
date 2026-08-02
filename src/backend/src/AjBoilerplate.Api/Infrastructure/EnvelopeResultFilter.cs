using AjBoilerplate.Contracts.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Wraps every controller-returned <see cref="IActionResult"/> in the <see cref="ApiResponse{T}"/>/
/// <see cref="ApiResponse"/> envelope, so no controller action ever builds the envelope itself and
/// no endpoint can accidentally ship an un-enveloped body.
///
/// Two shapes pass through untouched: a file download (already-correct binary content), and a 204
/// No Content — which by definition has no body, so there is nothing to envelope. A controller that
/// wants "success with a null payload" should return <c>Ok(null)</c>, not <c>NoContent()</c>.
/// </summary>
public sealed class EnvelopeResultFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        context.Result = Envelope(context.Result, context.HttpContext.TraceIdentifier);
        await next();
    }

    private static IActionResult Envelope(IActionResult result, string traceId) => result switch
    {
        FileResult => result,
        NoContentResult => result,
        ObjectResult { Value: ApiResponse } => result,
        ObjectResult { Value: not null } obj when IsGenericApiResponse(obj.Value!.GetType()) => result,
        CreatedAtActionResult created => WrapCreated(created, traceId),
        ObjectResult obj when (obj.StatusCode ?? StatusCodes.Status200OK) < 400 => WrapSuccess(obj.Value, obj.StatusCode ?? StatusCodes.Status200OK, traceId),
        ObjectResult obj => WrapObjectFailure(obj, traceId),
        StatusCodeResult status when status.StatusCode >= 400 => WrapFailure(status.StatusCode, traceId),
        _ => result,
    };

    private static bool IsGenericApiResponse(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ApiResponse<>);

    private static ObjectResult WrapSuccess(object? data, int statusCode, string traceId) =>
        new(new ApiResponse<object?> { IsSuccess = true, Data = data, StatusCode = statusCode, TraceId = traceId })
        {
            StatusCode = statusCode,
        };

    /// <summary>Builds the enveloped failure for <paramref name="statusCode"/>, optionally with a
    /// caller-supplied <paramref name="message"/> in place of the table's generic one.</summary>
    private static ObjectResult WrapFailure(int statusCode, string traceId, string? message = null)
    {
        var body = ApiResponse.CreateError(message ?? EnvelopeErrors.MessageFor(statusCode), EnvelopeErrors.CodeFor(statusCode));
        body.TraceId = traceId;
        return new ObjectResult(body) { StatusCode = statusCode };
    }

    private static ObjectResult WrapObjectFailure(ObjectResult obj, string traceId)
    {
        var statusCode = obj.StatusCode ?? StatusCodes.Status400BadRequest;

        // [ApiController]'s automatic model-binding validation (e.g. a malformed or type-mismatched
        // request body) short-circuits with a ValidationProblemDetails before any controller code —
        // or FluentValidation — ever runs. Flatten it exactly the way ValidationExceptionHandler
        // does, instead of falling through to a generic message that would leak the framework type
        // name via ToString().
        if (obj.Value is ValidationProblemDetails validationProblem)
        {
            var errors = validationProblem.Errors
                .SelectMany(kvp => kvp.Value.Select(message => $"{kvp.Key}: {message}"))
                .ToList();
            var body = ApiResponse.CreateError("One or more validation errors occurred.", EnvelopeCodes.ValidationError, errors);
            body.TraceId = traceId;
            return new ObjectResult(body) { StatusCode = statusCode };
        }

        // [ApiController]'s client-error filter rewrites every bare NotFound()/StatusCode(4xx) into a
        // ProblemDetails ObjectResult BEFORE this filter runs, so this — not the StatusCodeResult arm
        // above — is the path a controller's NotFound() actually takes. The status code drives the
        // envelope code, which is what makes a controller 404 and an unmatched-route 404 look
        // identical to a client. ProblemDetails' own Title ("Not Found") is discarded rather than
        // surfaced: it is framework wording, not a message written for a caller.
        if (obj.Value is ProblemDetails problem)
        {
            return WrapFailure(statusCode, traceId, problem.Detail);
        }

        // A controller returned a plain business object alongside a >=400 status — that object is the
        // point of the response, not framework noise, so carry it through as `data` instead of
        // discarding it behind a generic message. ValidationProblemDetails/ProblemDetails are handled
        // above and never reach here.
        if (obj.Value is not null)
        {
            var body = new ApiResponse<object?>
            {
                IsSuccess = false,
                Data = obj.Value,
                Message = EnvelopeErrors.MessageFor(statusCode),
                StatusCode = statusCode,
                Code = EnvelopeErrors.CodeFor(statusCode),
                TraceId = traceId,
            };
            return new ObjectResult(body) { StatusCode = statusCode };
        }

        return WrapFailure(statusCode, traceId);
    }

    private static CreatedAtActionResult WrapCreated(CreatedAtActionResult created, string traceId) =>
        new(created.ActionName, created.ControllerName, created.RouteValues,
            new ApiResponse<object?> { IsSuccess = true, Data = created.Value, StatusCode = StatusCodes.Status201Created, TraceId = traceId });
}
