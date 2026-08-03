using AjBoilerplate.Infrastructure.Features;
using Microsoft.Extensions.Configuration;

namespace AjBoilerplate.UnitTests.Features;

/// <summary>
/// The configuration-backed feature-flag reader. The property that matters is that everything
/// ambiguous is OFF: a flag is a switch for a feature that may not be finished, so the safe direction
/// to be wrong in is "not available".
/// </summary>
public sealed class ConfigurationFeatureFlagsTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void A_flag_configured_true_is_on(string value) =>
        Assert.True(FlagsWith(("NewCheckout", value)).IsEnabled("NewCheckout"));

    [Fact]
    public void A_flag_configured_false_is_off() =>
        Assert.False(FlagsWith(("NewCheckout", "false")).IsEnabled("NewCheckout"));

    [Fact]
    public void An_unconfigured_flag_is_off() =>
        // A flag that has not reached this environment yet must not be on in it.
        Assert.False(FlagsWith(("Other", "true")).IsEnabled("NewCheckout"));

    [Fact]
    public void A_flag_with_no_section_at_all_is_off() =>
        Assert.False(FlagsWith().IsEnabled("NewCheckout"));

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("")]
    [InlineData("  ")]
    public void A_value_that_is_not_a_boolean_is_off(string value) =>
        // "yes" is the realistic typo, and coercing it to true would switch on an unfinished feature
        // because someone wrote the wrong word in a config file.
        Assert.False(FlagsWith(("NewCheckout", value)).IsEnabled("NewCheckout"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_flag_name_is_off_rather_than_throwing(string? name) =>
        Assert.False(FlagsWith(("NewCheckout", "true")).IsEnabled(name!));

    [Fact]
    public void A_flag_name_is_matched_after_trimming() =>
        Assert.True(FlagsWith(("NewCheckout", "true")).IsEnabled("  NewCheckout  "));

    [Fact]
    public void A_flag_can_be_supplied_the_way_a_container_supplies_one()
    {
        // Environment variables are how a deployed instance actually sets these, and the double
        // underscore is the section separator. Worth pinning: it is the form an operator will type
        // under pressure during an incident.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:NewCheckout"] = "true" })
            .Build();

        Assert.True(new ConfigurationFeatureFlags(configuration).IsEnabled("NewCheckout"));
    }

    private static ConfigurationFeatureFlags FlagsWith(params (string Name, string Value)[] flags)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(flags.ToDictionary(
                f => $"{ConfigurationFeatureFlags.SectionName}:{f.Name}",
                f => (string?)f.Value))
            .Build();

        return new ConfigurationFeatureFlags(configuration);
    }
}
