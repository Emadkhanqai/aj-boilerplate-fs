using System.Security.Cryptography;
using System.Text;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Sessions;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Endpoints;

/// <summary>Trusted-caller endpoints, authenticated by the <c>X-Admin-Api-Key</c> header. Not reachable from a browser (nginx answers 404).</summary>
internal static class AdminEndpoints
{
    public const string ApiKeyHeader = "X-Admin-Api-Key";
    private const int MaxSubjectLength = 256;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/internal/admin").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var expected = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.Admin;

            // No key configured: the surface does not exist.
            if (!expected.IsEnabled)
            {
                return Results.NotFound();
            }

            if (!KeyMatches(http.Request.Headers[ApiKeyHeader].ToString(), expected.ApiKey))
            {
                return Results.Unauthorized();
            }

            http.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        admin.MapPost("/users/{subject}/refresh", RefreshAsync);
        return app;
    }

    /// <summary>Constant time, and independent of the supplied key's length: both sides are hashed first.</summary>
    internal static bool KeyMatches(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static async Task<IResult> RefreshAsync(string subject, ForceRefreshHandler handler, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > MaxSubjectLength)
        {
            return Results.BadRequest(new { error = "subject is required (at most 256 characters)" });
        }

        var flagged = await handler.HandleAsync(subject, cancellationToken);
        return Results.Ok(new RefreshResponse(flagged));
    }

    private sealed record RefreshResponse(int Flagged);
}
