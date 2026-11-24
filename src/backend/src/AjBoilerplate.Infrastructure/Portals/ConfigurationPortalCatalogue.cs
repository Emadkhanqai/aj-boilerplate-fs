using AjBoilerplate.Application.Portals;
using Microsoft.Extensions.Configuration;

namespace AjBoilerplate.Infrastructure.Portals;

/// <summary>
/// The portal list, read from configuration: <c>Portals:0:Key</c>, <c>Name</c>, <c>Url</c> and an optional
/// <c>RequiredCapability</c> (<c>read</c> | <c>write</c> | <c>administer</c>).
///
/// <para>
/// This is a stand-in, said plainly: on the USP platform the list of portals and who holds a seat in each
/// belongs to USP-Core's catalogue and the directory, and a boilerplate project outside USP has neither. A
/// project that does have a catalogue replaces this class behind <see cref="IPortalCatalogue"/>.
/// </para>
/// <para>
/// An entry with no key, name or url is skipped, and two entries with one key keep the first, so a
/// half-written configuration cannot produce a card nobody can open or a duplicate one.
/// </para>
/// </summary>
public sealed class ConfigurationPortalCatalogue : IPortalCatalogue
{
    private readonly IConfiguration _configuration;

    public ConfigurationPortalCatalogue(IConfiguration configuration) => _configuration = configuration;

    public IReadOnlyList<PortalEntry> All =>
        _configuration.GetSection("Portals").GetChildren()
            .Select(s => new PortalEntry(
                s["Key"]?.Trim() ?? string.Empty,
                s["Name"]?.Trim() ?? string.Empty,
                s["Url"]?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(s["RequiredCapability"]) ? null : s["RequiredCapability"]!.Trim()))
            .Where(p => p.Key.Length > 0 && p.Name.Length > 0 && p.Url.Length > 0)
            .DistinctBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
