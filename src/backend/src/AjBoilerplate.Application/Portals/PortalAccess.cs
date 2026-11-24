using AjBoilerplate.Application.Identity;

namespace AjBoilerplate.Application.Portals;

/// <summary>One entry of the configured portal list. <paramref name="RequiredCapability"/> is
/// <c>read</c>, <c>write</c> or <c>administer</c>; null means any signed-in person with a role.</summary>
public sealed record PortalEntry(string Key, string Name, string Url, string? RequiredCapability);

/// <summary>Where the portal list comes from. The boilerplate has no portal catalogue of its own (USP-Core owns
/// that on the USP platform), so the implementation reads the <c>Portals</c> configuration section.</summary>
public interface IPortalCatalogue
{
    IReadOnlyList<PortalEntry> All { get; }
}

public interface IPortalAccessService
{
    /// <summary>The portals the CURRENT caller may open, in catalogue order. Throws
    /// <see cref="Common.ForbiddenException"/> for a principal with no subject, as <c>/me</c> does.</summary>
    IReadOnlyList<PortalEntry> ForCaller();
}

public sealed class PortalAccessService : IPortalAccessService
{
    private readonly ICurrentUserService _currentUser;
    private readonly IPortalCatalogue _catalogue;

    public PortalAccessService(ICurrentUserService currentUser, IPortalCatalogue catalogue)
    {
        _currentUser = currentUser;
        _catalogue = catalogue;
    }

    public IReadOnlyList<PortalEntry> ForCaller()
    {
        var capabilities = _currentUser.GetProfile().Capabilities;
        return _catalogue.All.Where(p => Allows(p.RequiredCapability, capabilities)).ToList();
    }

    /// <summary>An unrecognised requirement grants nothing: a typo in configuration must hide a portal, never
    /// open it to everybody.</summary>
    internal static bool Allows(string? required, UserCapabilitiesDto capabilities) =>
        string.IsNullOrWhiteSpace(required)
            ? capabilities.CanView
            : required.Trim().ToLowerInvariant() switch
            {
                "read" => capabilities.CanView,
                "write" => capabilities.CanEdit,
                "administer" => capabilities.CanAdminister,
                _ => false,
            };
}
