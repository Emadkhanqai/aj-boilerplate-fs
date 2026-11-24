using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Configuration;

/// <summary>Start-up validation: a misconfigured instance refuses to start instead of failing per request.</summary>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    private readonly bool _isDevelopment;

    public AuthOptionsValidator(bool isDevelopment = false) => _isDevelopment = isDevelopment;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        ValidatePublicOrigin(options, failures);

        if (!RedirectTarget.IsSafe(options.LoggedOutPath))
        {
            failures.Add("Auth:LoggedOutPath must be a local absolute path such as /logged-out.");
        }

        ValidateUpstream("Auth:Entra:Authority", options.Entra.Authority, required: true, options, failures);
        ValidateUpstream("Auth:Entra:AuthorizeEndpointUrl", options.Entra.AuthorizeEndpointUrl, required: false, options, failures);
        ValidateUpstream("Auth:Entra:TokenEndpointUrl", options.Entra.TokenEndpointUrl, required: false, options, failures);

        if (!options.Entra.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("offline_access"))
        {
            failures.Add("Auth:Entra:Scopes must include offline_access: sessions cannot refresh without it.");
        }

        ValidateUpstream("Auth:Keycloak:BaseUrl", options.Keycloak.BaseUrl, required: true, options, failures);
        ValidateUpstream("Auth:Keycloak:TokenEndpointUrl", options.Keycloak.TokenEndpointUrl, required: false, options, failures);

        if (string.IsNullOrWhiteSpace(options.Keycloak.SubjectIssuer) && !_isDevelopment)
        {
            failures.Add(
                "Auth:Keycloak:SubjectIssuer is empty. Empty means an internal Keycloak-to-Keycloak exchange, "
                + "which is refused outside Development: set the alias of the identity provider in the realm.");
        }

        ValidateEncryptionKey(options.Session.EncryptionKey, failures);

        if (options.Session.RefreshSkew >= options.Session.IdleTimeout)
        {
            failures.Add("Auth:Session:RefreshSkew must be shorter than IdleTimeout.");
        }

        if (options.Session.AbsoluteLifetime < options.Session.IdleTimeout)
        {
            failures.Add("Auth:Session:AbsoluteLifetime must not be shorter than IdleTimeout.");
        }

        if (options.Session.RefreshWaitBudget > AuthSessionOptions.MaxRefreshWait)
        {
            failures.Add(
                $"Auth:Session:RefreshLockLifetime plus LockExpiryGrace is {options.Session.RefreshWaitBudget}, which is how long "
                + $"a request that lost the refresh lock holds the user's page. That exceeds the {AuthSessionOptions.MaxRefreshWait} ceiling.");
        }

        if (!options.Cookies.Secure && !_isDevelopment)
        {
            failures.Add("Auth:Cookies:Secure=false is refused outside Development.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void ValidatePublicOrigin(AuthOptions options, List<string> failures)
    {
        if (!Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var origin)
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || options.PublicOrigin.EndsWith('/'))
        {
            failures.Add("Auth:PublicOrigin must be an origin (scheme, host and optional port; no path, query or trailing slash), e.g. https://app.example.com.");
            return;
        }

        if (options.Cookies.Secure && origin.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("Auth:PublicOrigin must be https while Auth:Cookies:Secure is true.");
        }

        if (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp)
        {
            failures.Add("Auth:PublicOrigin must be an http or https origin.");
        }
    }

    /// <summary>
    /// An upstream receives a client secret or a user's token, so plain http to it is refused unless the host is
    /// Development or <c>Auth:AllowInsecureUpstreams</c> says otherwise.
    /// </summary>
    private void ValidateUpstream(string key, string url, bool required, AuthOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            if (required)
            {
                failures.Add($"{key} must be an absolute https URL.");
            }

            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add(required
                ? $"{key} must be an absolute https URL."
                : $"{key}, when set, must be an absolute http or https URL.");
        }
        else if (uri.Scheme == Uri.UriSchemeHttp && !options.AllowInsecureUpstreams && !_isDevelopment)
        {
            failures.Add(
                $"{key} is http. Plain http to an upstream that receives a client secret is refused "
                + "unless Auth:AllowInsecureUpstreams=true or the environment is Development.");
        }
    }

    private static void ValidateEncryptionKey(string key, List<string> failures)
    {
        try
        {
            if (Convert.FromBase64String(key.Trim()).Length == 32)
            {
                return;
            }
        }
        catch (FormatException)
        {
            // falls through to the failure below
        }

        failures.Add("Auth:Session:EncryptionKey must be base64 of exactly 32 bytes.");
    }
}
