namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Cross-origin policy for the deployed frontend. The API and the web app are separate origins, so
/// the browser enforces CORS on every call between them. Allowed origins come from the
/// <c>Cors:AllowedOrigins</c> config section — EMPTY by default, so an unconfigured environment
/// allows no cross-origin calls rather than silently allowing everything.
///
/// Credentials are never allowed: the frontend authenticates with a bearer token in the
/// <c>Authorization</c> header, not cookies, so the policy does not need <c>AllowCredentials</c> —
/// and adding it alongside a permissive origin list is the classic way this turns into a
/// vulnerability.
/// </summary>
public static class Cors
{
    public const string FrontendPolicy = "Frontend";

    public static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options =>
        {
            options.AddPolicy(FrontendPolicy, policy =>
            {
                policy.WithOrigins(allowedOrigins)
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                    .WithHeaders("Content-Type", "Authorization");
            });
        });

        return services;
    }
}
