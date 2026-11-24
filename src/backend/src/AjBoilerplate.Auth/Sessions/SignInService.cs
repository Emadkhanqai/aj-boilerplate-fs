using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>Sign-in start and completion (authorization code + PKCE), and sign-out.</summary>
public sealed partial class SignInService
{
    public const string CallbackPath = "/auth/callback";

    private readonly ISessionStore _sessions;
    private readonly ILoginStateStore _logins;
    private readonly IEntraClient _entra;
    private readonly IKeycloakExchanger _keycloak;
    private readonly AuthOptions _auth;
    private readonly PortalRegistry _portals;
    private readonly TimeProvider _time;
    private readonly ILogger<SignInService> _logger;

    public SignInService(
        ISessionStore sessions,
        ILoginStateStore logins,
        IEntraClient entra,
        IKeycloakExchanger keycloak,
        IOptions<AuthOptions> options,
        TimeProvider time,
        ILogger<SignInService> logger,
        PortalRegistry? portals = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _portals = portals ?? new PortalRegistry(options);
        _sessions = sessions;
        _logins = logins;
        _entra = entra;
        _keycloak = keycloak;
        _auth = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The redirect URI registered with the identity provider for a portal. Built from that portal's configured origin, never from a
    /// request header.
    /// </summary>
    public static Uri RedirectUriFor(PortalOptions portal)
    {
        ArgumentNullException.ThrowIfNull(portal);
        return new Uri($"{portal.Origin}{CallbackPath}");
    }

    /// <summary>The portal a request's host belongs to (portal #0 in a single-portal installation); <c>null</c> for an unknown host.</summary>
    public PortalOptions? Resolve(string? host) => _portals.Resolve(host);

    /// <param name="returnPath">Where to land after sign-in; sanitised by <see cref="RedirectTarget"/>.</param>
    /// <param name="prompt">What to ask the identity provider for.</param>
    /// <param name="cancellationToken">Cancels the login-state write.</param>
    /// <param name="loginHint">Optional e-mail typed on the app's own sign-in page. Personal data: never logged.</param>
    /// <param name="host">Browser-facing host; consulted only when several portals are configured. An unknown host throws:
    /// callers check <see cref="Resolve"/> first.</param>
    public async Task<LoginStart> BeginAsync(
        string? returnPath, SignInPrompt prompt, CancellationToken cancellationToken, string? loginHint = null, string? host = null)
    {
        var portal = _portals.Resolve(host) ?? throw new InvalidOperationException("The host is not a configured portal; check Resolve first.");
        var state = SessionIds.NewRandom();
        var verifier = SessionIds.NewRandom();
        var binding = SessionIds.NewRandom();

        await _logins.SaveAsync(
            state,
            new LoginState(verifier, RedirectTarget.OrDefault(returnPath), SessionIds.StorageKey(binding), portal.Host),
            _auth.Session.LoginStateLifetime,
            cancellationToken);

        var url = _entra.BuildAuthorizeUrl(RedirectUriFor(portal), state, SessionIds.CodeChallenge(verifier), prompt, LoginHints.Normalize(loginHint));
        return new LoginStart(url, binding);
    }

    public async Task<LoginCompletion> CompleteAsync(
        string? state, string? code, string? bindingValue, CancellationToken cancellationToken, string? host = null)
    {
        var portal = _portals.Resolve(host);
        if (portal is null || !SessionIds.IsWellFormed(state) || string.IsNullOrEmpty(code) || !SessionIds.IsWellFormed(bindingValue))
        {
            return LoginCompletion.Invalid;
        }

        // Single use: taken (read + deleted) before anything else, so a replayed callback finds nothing.
        var login = await _logins.TakeAsync(state!, cancellationToken);
        if (login is null
            || (login.Host is not null && !string.Equals(login.Host, portal.Host, StringComparison.OrdinalIgnoreCase))
            || !SessionIds.HashesEqual(login.BindingHash, SessionIds.StorageKey(bindingValue!)))
        {
            LogInvalidCallback(_logger);
            return LoginCompletion.Invalid;
        }

        var tokens = await _entra.RedeemCodeAsync(code, login.CodeVerifier, RedirectUriFor(portal), cancellationToken);
        switch (tokens.Outcome)
        {
            case EntraOutcome.InvalidGrant:
                return LoginCompletion.Invalid;
            case EntraOutcome.Transient:
                return LoginCompletion.Unavailable;
        }

        var exchange = await _keycloak.ExchangeAsync(tokens.AccessToken!, portal, cancellationToken);
        switch (exchange.Outcome)
        {
            case ExchangeOutcome.Denied:
                LogExchangeDenied(_logger, portal.ClientId);
                return LoginCompletion.NoAccess;
            case ExchangeOutcome.Transient:
                return LoginCompletion.Unavailable;
        }

        var claims = JwtClaims.TryParse(exchange.Jwt);
        if (claims is null)
        {
            LogUnreadableJwt(_logger);
            return LoginCompletion.Unavailable;
        }

        // Two different refusals: a token minted for another client says OUR configuration is wrong; a token
        // with no role says this user has no seat here.
        if (claims.IsForAnotherClient(portal.ClientId))
        {
            LogWrongAuthorizedParty(_logger, claims.AuthorizedParty!, portal.ClientId);
            return LoginCompletion.NoAccess;
        }

        if (claims.ClientRoles(portal.ClientId).Count == 0)
        {
            LogNoRole(_logger, portal.ClientId);
            return LoginCompletion.NoAccess;
        }

        var sessionId = SessionIds.NewRandom();
        var session = new AuthSession(
            exchange.Jwt!,
            claims.ExpiresAt,
            tokens.RefreshToken,
            claims.Subject,
            portal.ClientId,
            ForceRefresh: false,
            _time.GetUtcNow());

        await _sessions.CreateAsync(SessionIds.StorageKey(sessionId), session, _auth.Session.IdleTimeout, cancellationToken);
        LogSignedIn(_logger, portal.ClientId);
        return new LoginCompletion(LoginCompletionStatus.SignedIn, sessionId, login.ReturnPath);
    }

    public async Task SignOutAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (!SessionIds.IsWellFormed(sessionId))
        {
            return;
        }

        var key = SessionIds.StorageKey(sessionId!);
        var session = await _sessions.GetAsync(key, cancellationToken);
        if (session is not null)
        {
            // An END, not a delete: the event is queued in the same transaction as the delete and sent later by the
            // dispatcher, so the logout never waits on, or fails because of, a portal.
            await _sessions.EndAsync(key, session.Subject, SessionEnd.LoggedOut(_time.GetUtcNow()), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a sign-in callback: unknown, expired or replayed state, host mismatch or browser-binding mismatch.")]
    private static partial void LogInvalidCallback(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in refused: Keycloak denied the token exchange for client {ClientId}.")]
    private static partial void LogExchangeDenied(ILogger logger, string clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in refused: Keycloak minted that token for client {AuthorizedParty}, not for {ClientId}, so its roles are not this app's to read. Check the client id and its client-roles mapper.")]
    private static partial void LogWrongAuthorizedParty(ILogger logger, string authorizedParty, string clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in refused: the exchange succeeded but the token carries no role for client {ClientId}.")]
    private static partial void LogNoRole(ILogger logger, string clientId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak returned a token without a readable sub/exp payload.")]
    private static partial void LogUnreadableJwt(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session created for client {ClientId}.")]
    private static partial void LogSignedIn(ILogger logger, string clientId);
}
