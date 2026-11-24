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

        if (!Uri.TryCreate(options.Entra.Authority, UriKind.Absolute, out var entra) || entra.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("Auth:Entra:Authority must be an absolute https URL.");
        }

        if (!options.Entra.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("offline_access"))
        {
            failures.Add("Auth:Entra:Scopes must include offline_access: sessions cannot refresh without it.");
        }

        if (!Uri.TryCreate(options.Keycloak.BaseUrl, UriKind.Absolute, out var kc) || kc.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("Auth:Keycloak:BaseUrl must be an absolute https URL.");
        }

        ValidateTokenEndpoint(options, failures);
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

    private void ValidateTokenEndpoint(AuthOptions options, List<string> failures)
    {
        var url = options.Keycloak.TokenEndpointUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var token)
            || (token.Scheme != Uri.UriSchemeHttps && token.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add("Auth:Keycloak:TokenEndpointUrl, when set, must be an absolute http or https URL.");
        }
        else if (token.Scheme == Uri.UriSchemeHttp && !options.AllowInsecureUpstreams && !_isDevelopment)
        {
            failures.Add(
                "Auth:Keycloak:TokenEndpointUrl is http. Plain http to an upstream that receives a client secret is refused "
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
