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

        ValidatePortals(options, failures);
        ValidateAdmin(options, failures);
        ValidateEvents(options, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void ValidatePortals(AuthOptions options, List<string> failures)
    {
        ValidateSessionEvents("Auth:Portal:SessionEvents", options.Portal.SessionEvents, options, failures);

        var hosts = new HashSet<string>(StringComparer.Ordinal);
        if (Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var origin))
        {
            hosts.Add(origin.Host.ToLowerInvariant());
        }

        var clientIds = new HashSet<string>(StringComparer.Ordinal) { options.Portal.ClientId };
        for (var i = 0; i < options.Portals.Count; i++)
        {
            var portal = options.Portals[i];
            var key = $"Auth:Portals:{i}";
            if (string.IsNullOrWhiteSpace(portal.ClientId) || string.IsNullOrWhiteSpace(portal.ClientSecret))
            {
                failures.Add($"{key}:ClientId and ClientSecret are required.");
            }
            else if (!clientIds.Add(portal.ClientId))
            {
                failures.Add($"{key}:ClientId '{portal.ClientId}' is already used by another portal.");
            }

            if (!HostName.IsValid(portal.Host))
            {
                failures.Add($"{key}:Host must be a bare DNS host name (no scheme, no port, no path), e.g. app2.example.com.");
            }
            else if (!hosts.Add(portal.Host.ToLowerInvariant()))
            {
                failures.Add($"{key}:Host '{portal.Host}' is already used by another portal (portal #0's host is Auth:PublicOrigin's).");
            }

            if (!string.IsNullOrEmpty(portal.LoggedOutPath) && !RedirectTarget.IsSafe(portal.LoggedOutPath))
            {
                failures.Add($"{key}:LoggedOutPath must be a local absolute path such as /logged-out.");
            }

            ValidateSessionEvents($"{key}:SessionEvents", portal.SessionEvents, options, failures);
        }
    }

    private void ValidateSessionEvents(string key, SessionEventsOptions events, AuthOptions options, List<string> failures)
    {
        if (!events.IsConfigured)
        {
            if (!string.IsNullOrWhiteSpace(events.Audience))
            {
                failures.Add($"{key}:Audience is set but {key}:Url is not; set both or neither.");
            }

            if (events.UsesKey)
            {
                failures.Add($"{key}:Key is set but {key}:Url is not; set both or neither.");
            }

            return;
        }

        if (!Uri.TryCreate(events.Url, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment))
        {
            failures.Add($"{key}:Url must be an absolute http or https URL with no query.");
            return;
        }

        // An audience with a path is refused by a Google-IAM-fronted receiver, indistinguishably from a missing invoker grant:
        // refuse it here, where the message can say which it is.
        if (!Uri.TryCreate(events.ResolvedAudience, UriKind.Absolute, out var audience)
            || audience.AbsolutePath != "/"
            || !string.IsNullOrEmpty(audience.Query))
        {
            failures.Add($"{key}:Audience must be an origin (scheme and host, no path), e.g. https://api.example.com.");
        }

        // The call carries a shared key or a Google ID token: plain http would put either on the wire in the clear.
        ValidateUpstream($"{key}:Url", events.Url, required: true, options, failures);
    }

    private void ValidateAdmin(AuthOptions options, List<string> failures)
    {
        var admin = options.Admin;
        ValidateUpstream("Auth:Admin:Keycloak:BaseUrl", admin.Keycloak.BaseUrl, required: false, options, failures);
        if (!string.IsNullOrWhiteSpace(admin.Audience) != (admin.AllowedCallers.Count > 0))
        {
            failures.Add("Auth:Admin:Audience and Auth:Admin:AllowedCallers must be set together or not at all.");
        }

        foreach (var path in admin.ManagedGroupPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.Length < 2 || path.EndsWith('/'))
            {
                failures.Add($"Auth:Admin:ManagedGroupPaths entry '{path}' must be a Keycloak group path such as /my-app (leading slash, no trailing slash).");
            }
        }

        if (!string.IsNullOrWhiteSpace(admin.Keycloak.ClientId) != !string.IsNullOrWhiteSpace(admin.Keycloak.ClientSecret))
        {
            failures.Add("Auth:Admin:Keycloak:ClientId and ClientSecret must be set together or not at all.");
        }
    }

    private static void ValidateEvents(AuthOptions options, List<string> failures)
    {
        var events = options.Events;
        if (!string.IsNullOrWhiteSpace(events.Audience) != !string.IsNullOrWhiteSpace(events.PushServiceAccount))
        {
            failures.Add("Auth:Events:Audience and Auth:Events:PushServiceAccount must be set together or not at all.");
        }
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
