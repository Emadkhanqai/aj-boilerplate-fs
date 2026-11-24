using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Configuration;

/// <summary>
/// The portals this Auth service signs users in to. Portal #0 is <c>Auth:Portal</c> (its host is <c>Auth:PublicOrigin</c>'s);
/// <c>Auth:Portals</c> adds more, each on its own host.
/// </summary>
/// <remarks>
/// <para>
/// <b>A single-portal installation never looks at a host.</b> With <c>Auth:Portals</c> empty every request is portal #0, exactly
/// as before the registry existed: <see cref="Resolve"/> ignores its argument, so a missing or odd forwarded-host header
/// cannot turn a working installation into a 404. Only once a second portal is configured does the host decide, and an
/// unknown host then resolves to nothing.
/// </para>
/// <para>
/// The redirect URI is built from the RESOLVED portal's configured origin, never from the header that chose it.
/// </para>
/// </remarks>
public sealed class PortalRegistry
{
    private readonly Dictionary<string, PortalOptions> _byHost = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PortalOptions> _byClientId = new(StringComparer.Ordinal);

    public PortalRegistry(IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var auth = options.Value;

        var origin = Uri.TryCreate(auth.PublicOrigin, UriKind.Absolute, out var parsed) ? parsed : null;
        Primary = new PortalOptions
        {
            ClientId = auth.Portal.ClientId,
            ClientSecret = auth.Portal.ClientSecret,
            Host = origin?.Host.ToLowerInvariant() ?? string.Empty,
            LoggedOutPath = auth.LoggedOutPath,
            DisplayName = auth.Portal.DisplayName,
            SessionEvents = auth.Portal.SessionEvents,
            OriginOverride = auth.PublicOrigin,
        };

        All = [Primary, .. auth.Portals];

        // First wins on a duplicate rather than throwing here: AuthOptionsValidator refuses a duplicated host or client id at
        // start-up with a message naming the row, which a throw from a constructor would not.
        foreach (var portal in All)
        {
            _byHost.TryAdd(portal.Host.ToLowerInvariant(), portal);
            _byClientId.TryAdd(portal.ClientId, portal);
        }
    }

    /// <summary>Portal #0 as the registry sees it: <c>Auth:Portal</c> with the host, origin and logged-out page of the service itself.</summary>
    public PortalOptions Primary { get; }

    public IReadOnlyList<PortalOptions> All { get; }

    /// <summary>False only when <c>Auth:Portals</c> is empty.</summary>
    public bool IsMultiPortal => All.Count > 1;

    /// <summary>The portal a request belongs to: portal #0 for a single-portal installation, else the portal whose host it came in on.</summary>
    public PortalOptions? Resolve(string? host) => IsMultiPortal ? FindByHost(host) : Primary;

    public PortalOptions? FindByHost(string? host)
    {
        var normalized = HostName.Normalize(host);
        return normalized is not null && _byHost.TryGetValue(normalized, out var portal) ? portal : null;
    }

    /// <summary>
    /// The portal a hand-off names by its Keycloak client id. An exact, case-sensitive key into the configured list: never a URL,
    /// so <c>/auth/handoff?to=</c> cannot be made to point anywhere that is not a configured portal.
    /// </summary>
    public PortalOptions? FindByClientId(string? clientId) =>
        !string.IsNullOrEmpty(clientId) && _byClientId.TryGetValue(clientId, out var portal) ? portal : null;
}
