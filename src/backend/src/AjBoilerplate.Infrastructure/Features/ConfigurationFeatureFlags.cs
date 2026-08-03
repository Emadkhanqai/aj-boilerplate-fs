using AjBoilerplate.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace AjBoilerplate.Infrastructure.Features;

/// <summary>
/// <see cref="IFeatureFlags"/> backed by the <c>Features</c> configuration section. No third-party
/// dependency, on purpose: a boilerplate should not pick a flag vendor for you, and configuration is
/// already the one mechanism every deployment target here can supply — appsettings, an environment
/// variable (<c>Features__NewCheckout=true</c>), or the cloud secret store loaded at boot.
///
/// <code>
/// "Features": {
///   "NewCheckout": true
/// }
/// </code>
///
/// <para>
/// Read through <see cref="IConfiguration"/> on every call rather than snapshotted into an options
/// object. That is what makes a flag flippable at run time: the configuration providers this host
/// uses support reload-on-change, so an operator can turn a feature off during an incident without a
/// redeploy. The lookup is a dictionary hit against already-parsed configuration — cheap enough that
/// caching it would trade the only property that makes the flag useful for nothing measurable.
/// </para>
///
/// <para>
/// To move to a real flag platform, implement <see cref="IFeatureFlags"/> over its SDK and change the
/// one registration in <c>AddInfrastructure</c>. Nothing that reads a flag changes.
/// </para>
/// </summary>
public sealed class ConfigurationFeatureFlags : IFeatureFlags
{
    /// <summary>The configuration section flags are read from.</summary>
    public const string SectionName = "Features";

    private readonly IConfiguration _configuration;

    public ConfigurationFeatureFlags(IConfiguration configuration) => _configuration = configuration;

    public bool IsEnabled(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // GetValue<bool> with an explicit false default, so a missing key, an empty string, and a
        // value that is not a boolean at all ("yes", "1 ", a typo) are all OFF rather than throwing
        // or, worse, being coerced to on. An unparseable value would otherwise fail at the call site
        // of whatever happened to read the flag first, far from the configuration that caused it.
        var value = _configuration.GetSection(SectionName)[name.Trim()];

        return bool.TryParse(value, out var enabled) && enabled;
    }
}
