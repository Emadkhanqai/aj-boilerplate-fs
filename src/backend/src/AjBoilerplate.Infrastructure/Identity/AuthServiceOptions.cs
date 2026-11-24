namespace AjBoilerplate.Infrastructure.Identity;

/// <summary>
/// How this API reaches the Auth service's internal admin surface (<c>/internal/admin/*</c>). Under
/// <c>AUTH_PROVIDER=usp</c> there is no Auth service of the project's own to call, so these stay blank and the
/// admin user endpoints answer 503.
/// </summary>
public sealed class AuthServiceOptions
{
    public const string SectionName = "AuthService";

    /// <summary>The Auth service's own origin, reachable from this API (not through the browser gateway,
    /// which answers 404 for <c>/internal/</c>).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The shared secret sent as <c>X-Admin-Api-Key</c>. Comes from the secrets provider; never
    /// logged and never put in a file. Must equal the Auth service's <c>Auth:Admin:ApiKey</c>.</summary>
    public string AdminApiKey { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan EffectiveTimeout =>
        Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(15) : Timeout;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(AdminApiKey);
}
