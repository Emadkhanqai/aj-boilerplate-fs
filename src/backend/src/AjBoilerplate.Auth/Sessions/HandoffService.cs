using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>
/// Cross-portal sign-in hand-off (docs/auth/handoff.md). A signed-in session on one portal mints a one-time
/// ticket; the target portal redeems it into a session of its own, with no identity-provider round trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing on this path ever ends a session.</b> Every refusal answers
/// <see cref="HandoffRedemptionStatus.Fallback"/>, which the endpoint turns into the ordinary
/// <c>/auth/login</c>, so the worst a fault here can do is put the user back where they were before
/// this feature existed. The single exception to "fall back" is a definitive Keycloak no-role,
/// which is the same 403 page as the sign-in callback's, and for the same reason: a
/// redirect would loop through Entra's silent SSO.
/// </para>
/// <para>
/// <b>The ticket value is never logged</b>, nor its hash; every log line carries a reason slug and
/// client ids only. HandoffEndpointTests captures every line at every level and asserts that.
/// </para>
/// </remarks>
public sealed partial class HandoffService
{
    /// <summary>The redeem path on the TARGET host. Routed to this service by nginx's <c>/auth/</c>.</summary>
    public const string RedeemPath = "/auth/handoff/redeem";

    private readonly ISessionStore _sessions;
    private readonly IHandoffTicketStore _tickets;
    private readonly IDistributedLock _locks;
    private readonly IEntraClient _entra;
    private readonly IKeycloakExchanger _keycloak;
    private readonly PortalRegistry _portals;
    private readonly AuthSessionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HandoffService> _logger;
    private readonly LineageExchange _lineage;

    public HandoffService(
        ISessionStore sessions,
        IHandoffTicketStore tickets,
        IDistributedLock locks,
        IEntraClient entra,
        IKeycloakExchanger keycloak,
        PortalRegistry portals,
        IOptions<AuthOptions> options,
        TimeProvider time,
        ILogger<HandoffService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessions = sessions;
        _tickets = tickets;
        _locks = locks;
        _entra = entra;
        _keycloak = keycloak;
        _portals = portals;
        _options = options.Value.Session;
        _time = time;
        _logger = logger;
        _lineage = new LineageExchange(sessions, locks, entra, keycloak, _options, time);
    }

    /// <summary><c>GET /auth/handoff?to=&amp;rd=</c> on the SOURCE host.</summary>
    /// <param name="sourceHost">Browser-facing host, from the forwarded-host header.</param>
    /// <param name="targetClientId">The <c>to</c> parameter: a Keycloak client id from the allow-list.</param>
    /// <param name="returnPath">Where to land on the target; sanitised by <see cref="RedirectTarget"/>.</param>
    /// <param name="sessionId">The source host's session cookie.</param>
    /// <param name="fetchSite">The request's <c>Sec-Fetch-Site</c>.</param>
    /// <param name="cancellationToken">Cancels the store calls.</param>
    public async Task<HandoffStart> BeginAsync(
        string? sourceHost,
        string? targetClientId,
        string? returnPath,
        string? sessionId,
        string? fetchSite,
        CancellationToken cancellationToken)
    {
        var source = _portals.Resolve(sourceHost);
        if (source is null)
        {
            return new HandoffStart(HandoffStartStatus.UnknownSource);
        }

        var target = _portals.FindByClientId(targetClientId);
        if (target is null)
        {
            // The key is user input: logged only when it is id-shaped, so a crafted value cannot
            // write into the log. A broken menu link is what this line exists to find.
            LogUnknownTarget(_logger, IsKeyShaped(targetClientId) ? targetClientId! : "<malformed>");
            return new HandoffStart(HandoffStartStatus.UnknownTarget);
        }

        var path = RedirectTarget.OrDefault(returnPath);
        if (ReferenceEquals(source, target))
        {
            return new HandoffStart(HandoffStartStatus.SamePortal, target.Origin, path);
        }

        // docs/auth/handoff.md, "Starting a hand-off from a third-party page". A third-party page can navigate the victim here; minting would cost an Entra
        // refresh per hit and gain the attacker nothing, so it is simply the ordinary sign-in.
        if (string.Equals(fetchSite, "cross-site", StringComparison.Ordinal))
        {
            LogRefused(_logger, "/auth/handoff", "cross_site_start", target.ClientId);
            return new HandoffStart(HandoffStartStatus.Fallback, target.Origin, path);
        }

        if (!SessionIds.IsWellFormed(sessionId))
        {
            LogRefused(_logger, "/auth/handoff", "no_source_session", target.ClientId);
            return new HandoffStart(HandoffStartStatus.Fallback, target.Origin, path);
        }

        var sessionKey = SessionIds.StorageKey(sessionId!);
        var session = await _sessions.GetAsync(sessionKey, cancellationToken);
        var refusal = session is null ? "no_source_session" : SourceRefusal(session, source);
        if (refusal is not null)
        {
            LogRefused(_logger, "/auth/handoff", refusal, target.ClientId);
            return new HandoffStart(HandoffStartStatus.Fallback, target.Origin, path);
        }

        var ticket = SessionIds.NewRandom();
        await _tickets.SaveAsync(
            SessionIds.StorageKey(ticket),
            new HandoffTicket(sessionKey, session!.Subject, source.ClientId, source.Origin, target.ClientId, path),
            _options.HandoffTicketLifetime,
            cancellationToken);

        LogStarted(_logger, source.ClientId, target.ClientId);
        return new HandoffStart(HandoffStartStatus.Ticket, target.Origin, path, ticket);
    }

    /// <summary><c>POST /auth/handoff/redeem</c> on the TARGET host.</summary>
    /// <param name="targetHost">Browser-facing host, from the forwarded-host header.</param>
    /// <param name="ticketValue">The posted ticket.</param>
    /// <param name="origin">The request's <c>Origin</c> header; must be the ticket's source origin.</param>
    /// <param name="existingSessionId">The target host's session cookie, if the browser has one.</param>
    /// <param name="cancellationToken">Cancels the store and upstream calls.</param>
    public async Task<HandoffRedemption> RedeemAsync(
        string? targetHost,
        string? ticketValue,
        string? origin,
        string? existingSessionId,
        CancellationToken cancellationToken)
    {
        var portal = _portals.Resolve(targetHost);
        if (portal is null)
        {
            return HandoffRedemption.UnknownHost;
        }

        if (!SessionIds.IsWellFormed(ticketValue))
        {
            return Refuse("ticket_malformed", portal, RedirectTarget.Default);
        }

        // Single use: taken (read + deleted) BEFORE any other check, so every presentation spends
        // it, including one refused below.
        var ticket = await _tickets.TakeAsync(SessionIds.StorageKey(ticketValue!), cancellationToken);
        if (ticket is null)
        {
            return Refuse("ticket_unknown_used_or_expired", portal, RedirectTarget.Default);
        }

        var path = RedirectTarget.OrDefault(ticket.ReturnPath);
        if (!string.Equals(ticket.TargetClientId, portal.ClientId, StringComparison.Ordinal))
        {
            return Refuse("ticket_for_another_host", portal, path);
        }

        // Login CSRF (docs/auth/handoff.md): a ticket minted from an attacker's own session must not be
        // redeemable from a page the attacker controls. Origin is a header no other origin can set.
        if (!string.Equals(origin, ticket.SourceOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(string.IsNullOrEmpty(origin) || origin == "null" ? "origin_absent" : "origin_mismatch", portal, path);
        }

        var source = await _sessions.GetAsync(ticket.SourceSessionKey, cancellationToken);
        var sourcePortal = _portals.FindByClientId(ticket.SourceClientId);
        var refusal = source is null || sourcePortal is null ? "source_session_gone" : SourceRefusal(source, sourcePortal);
        if (refusal is null && !string.Equals(source!.Subject, ticket.Subject, StringComparison.Ordinal))
        {
            refusal = "subject_mismatch";
        }

        if (refusal is not null)
        {
            return Refuse(refusal, portal, path);
        }

        // The new session is bounded by the ORIGINAL sign-in, never restarted by a hand-off.
        var now = _time.GetUtcNow();
        var signedInAt = source!.CreatedAt > DateTimeOffset.UnixEpoch ? source.CreatedAt : now;

        if (await HasLiveSessionAsync(existingSessionId, portal, source.Subject, cancellationToken))
        {
            LogCompleted(_logger, portal.ClientId, reused: true);
            return new HandoffRedemption(HandoffRedemptionStatus.Reused, ReturnPath: path);
        }

        var rootKey = source.RefreshVia ?? ticket.SourceSessionKey;
        var handle = await _lineage.AcquireAsync(rootKey, cancellationToken);
        if (handle is null)
        {
            return Refuse("lineage_refresh_busy", portal, path);
        }

        await using (handle)
        {
            // Lock, Entra refresh with write-back, Keycloak exchange and the claim checks are shared
            // with the cross-portal API route (LineageExchange). The reason slugs are unchanged.
            var mint = await _lineage.MintUnderLockAsync(rootKey, source.Subject, portal, cancellationToken);
            switch (mint.Status)
            {
                case LineageMintStatus.NoAccess:
                    LogNoAccess(_logger, mint.Reason, portal.ClientId);
                    return HandoffRedemption.NoAccess;
                case LineageMintStatus.Refused:
                    if (mint.Reason == LineageMint.SubjectMismatch)
                    {
                        LogSubjectDiverged(_logger, portal.ClientId);
                    }

                    return Refuse(mint.Reason, portal, path);
            }

            var claims = mint.Claims!;
            var sessionId = SessionIds.NewRandom();
            await _sessions.CreateAsync(
                SessionIds.StorageKey(sessionId),
                new AuthSession(
                    mint.Jwt!,
                    claims.ExpiresAt,
                    EntraRefreshToken: null,
                    source.Subject,
                    portal.ClientId,
                    ForceRefresh: false,
                    signedInAt,
                    RefreshVia: rootKey),
                _options.IdleTimeout,
                cancellationToken);
            await _sessions.TouchAsync(rootKey, source.Subject, _options.IdleTimeout, cancellationToken);

            LogCompleted(_logger, portal.ClientId, reused: false);
            return new HandoffRedemption(HandoffRedemptionStatus.SignedIn, sessionId, path);
        }
    }

    /// <summary>Why a session may not be the source of a hand-off, or null when it may.</summary>
    private string? SourceRefusal(AuthSession session, PortalOptions sourcePortal)
    {
        if (!string.Equals(session.ClientId, sourcePortal.ClientId, StringComparison.Ordinal))
        {
            return "source_session_portal_mismatch";
        }

        return session.CreatedAt > DateTimeOffset.UnixEpoch && _time.GetUtcNow() - session.CreatedAt >= _options.AbsoluteLifetime
            ? "source_past_absolute_lifetime"
            : null;
    }

    private async Task<bool> HasLiveSessionAsync(string? sessionId, PortalOptions portal, string subject, CancellationToken cancellationToken)
    {
        if (!SessionIds.IsWellFormed(sessionId))
        {
            return false;
        }

        var existing = await _sessions.GetAsync(SessionIds.StorageKey(sessionId!), cancellationToken);
        return existing is not null
            && string.Equals(existing.Subject, subject, StringComparison.Ordinal)
            && SourceRefusal(existing, portal) is null;
    }

    private HandoffRedemption Refuse(string reason, PortalOptions portal, string returnPath)
    {
        LogRefused(_logger, RedeemPath, reason, portal.ClientId);
        return new HandoffRedemption(HandoffRedemptionStatus.Fallback, ReturnPath: returnPath);
    }

    private static bool IsKeyShaped(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 64
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    // {Endpoint} is a PARAMETER: one helper serves both surfaces, and a surface baked into a shared
    // template is the false citation the logging rules record.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Portal hand-off refused at {Endpoint}: {Reason} (target client {TargetClientId}); the browser was sent to the ordinary sign-in.")]
    private static partial void LogRefused(ILogger logger, string endpoint, string reason, string targetClientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Portal hand-off asked for an unknown portal key {PortalKey}; check the link that sent it.")]
    private static partial void LogUnknownTarget(ILogger logger, string portalKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Portal hand-off ticket issued from client {SourceClientId} to client {TargetClientId}.")]
    private static partial void LogStarted(ILogger logger, string sourceClientId, string targetClientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Portal hand-off redeemed for client {TargetClientId}; existing session reused: {Reused}.")]
    private static partial void LogCompleted(ILogger logger, string targetClientId, bool reused);

    [LoggerMessage(Level = LogLevel.Information, Message = "Portal hand-off refused with no access ({Reason}) for client {TargetClientId}: 403, not a redirect.")]
    private static partial void LogNoAccess(ILogger logger, string reason, string targetClientId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Portal hand-off for client {TargetClientId}: Keycloak minted a token for a different subject than the source session's. Refused.")]
    private static partial void LogSubjectDiverged(ILogger logger, string targetClientId);
}

public enum HandoffStartStatus
{
    /// <summary>The request host is not a configured portal → 404.</summary>
    UnknownSource,

    /// <summary><c>to</c> names no configured portal → 404 page with a way home.</summary>
    UnknownTarget,

    /// <summary><c>to</c> is this portal → 303 to the return path here.</summary>
    SamePortal,

    /// <summary>No ticket: 303 to the ordinary sign-in on the target host.</summary>
    Fallback,

    /// <summary>Ticket minted: render the auto-submitting POST to the target.</summary>
    Ticket,
}

/// <param name="Status">What to answer.</param>
/// <param name="TargetOrigin">The target portal's browser origin, when there is one.</param>
/// <param name="ReturnPath">Sanitised return path on the target.</param>
/// <param name="Ticket">The ticket VALUE, only for <see cref="HandoffStartStatus.Ticket"/>. Goes into a
/// POST body, never a URL or a log.</param>
public sealed record HandoffStart(HandoffStartStatus Status, string? TargetOrigin = null, string ReturnPath = RedirectTarget.Default, string? Ticket = null);

public enum HandoffRedemptionStatus
{
    /// <summary>The request host is not a configured portal → 404.</summary>
    UnknownHost,

    /// <summary>New session minted: set the cookie, 303 to the return path.</summary>
    SignedIn,

    /// <summary>The browser already holds a live session here for the same person: 303 to the return path.</summary>
    Reused,

    /// <summary>Keycloak's definitive no-role → 403 page, not a redirect.</summary>
    NoAccess,

    /// <summary>Anything else → 303 to the ordinary <c>/auth/login?rd=</c> on this host.</summary>
    Fallback,
}

public sealed record HandoffRedemption(HandoffRedemptionStatus Status, string? SessionId = null, string ReturnPath = RedirectTarget.Default)
{
    public static HandoffRedemption UnknownHost { get; } = new(HandoffRedemptionStatus.UnknownHost);

    public static HandoffRedemption NoAccess { get; } = new(HandoffRedemptionStatus.NoAccess);
}
