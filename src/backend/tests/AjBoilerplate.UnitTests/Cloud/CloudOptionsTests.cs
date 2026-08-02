using AjBoilerplate.Infrastructure.Cloud;

namespace AjBoilerplate.UnitTests.Cloud;

/// <summary>
/// The CLOUD_PROVIDER switch. Getting this wrong silently would mean reading secrets from the wrong
/// cloud — or from none — so the parse is required to fail loudly instead of defaulting.
/// </summary>
public sealed class CloudOptionsTests
{
    [Theory]
    [InlineData("gcp", CloudProvider.Gcp)]
    [InlineData("GCP", CloudProvider.Gcp)]
    [InlineData("  azure  ", CloudProvider.Azure)]
    [InlineData("Azure", CloudProvider.Azure)]
    public void Resolve_accepts_either_provider_in_any_casing(string configured, CloudProvider expected) =>
        Assert.Equal(expected, new CloudOptions { Provider = configured }.Resolve());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_defaults_to_gcp_when_blank(string configured) =>
        Assert.Equal(CloudProvider.Gcp, new CloudOptions { Provider = configured }.Resolve());

    [Fact]
    public void Resolve_throws_on_an_unrecognised_provider()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new CloudOptions { Provider = "aws" }.Resolve());

        // The message must name the offending value AND the supported ones, or the operator is
        // left guessing at a startup failure.
        Assert.Contains("aws", error.Message, StringComparison.Ordinal);
        Assert.Contains("gcp", error.Message, StringComparison.Ordinal);
        Assert.Contains("azure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_instance_selects_gcp() =>
        Assert.Equal(CloudProvider.Gcp, new CloudOptions().Resolve());
}
