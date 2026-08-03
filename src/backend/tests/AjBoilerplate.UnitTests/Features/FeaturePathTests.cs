using AjBoilerplate.Domain.Features;

namespace AjBoilerplate.UnitTests.Features;

/// <summary>
/// The path canonicaliser that every page-targeting comparison runs through. Most of this is
/// tidiness; the <c>..</c> cases are a security control, and are the reason the comparison downstream
/// is allowed to be a plain <c>StartsWith</c>.
/// </summary>
public sealed class FeaturePathTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_path_is_the_root(string? path) =>
        Assert.Equal("/", FeaturePath.Normalize(path));

    [Theory]
    [InlineData("/reports", "/reports")]
    [InlineData("/reports/monthly", "/reports/monthly")]
    [InlineData("  /reports  ", "/reports")]
    public void An_ordinary_path_is_returned_as_is(string path, string expected) =>
        Assert.Equal(expected, FeaturePath.Normalize(path));

    [Theory]
    [InlineData("/reports?tab=1", "/reports")]
    [InlineData("/reports#section", "/reports")]
    [InlineData("/reports?tab=1#section", "/reports")]
    [InlineData("/reports#a?b", "/reports")]
    [InlineData("?tab=1", "/")]
    public void The_query_string_and_fragment_are_stripped(string path, string expected) =>
        // Only the path targets a page. Leaving a query string on would let two visits to the same
        // page match differently, and would let caller-controlled text influence a prefix comparison.
        Assert.Equal(expected, FeaturePath.Normalize(path));

    [Theory]
    [InlineData("/reports//monthly", "/reports/monthly")]
    [InlineData("/reports/", "/reports")]
    [InlineData("///", "/")]
    [InlineData("/reports/./monthly", "/reports/monthly")]
    [InlineData("/reports/.", "/reports")]
    public void Empty_and_dot_segments_collapse(string path, string expected) =>
        Assert.Equal(expected, FeaturePath.Normalize(path));

    [Theory]
    [InlineData("/reports/../admin", "/admin")]
    [InlineData("/reports/monthly/../../admin", "/admin")]
    [InlineData("/reports/..", "/")]
    [InlineData("/reports/../../../../admin", "/admin")]
    [InlineData("/../admin", "/admin")]
    [InlineData("..", "/")]
    [InlineData("/reports/../admin?x=1", "/admin")]
    public void Parent_segments_are_resolved_and_can_never_escape_the_root(string path, string expected) =>
        // THE point of this type. A path like "/reports/../admin" literally starts with "/reports",
        // so an unresolved comparison would fire a reports-scoped announcement on the admin route —
        // which is where that URL actually lands. It also must not be possible to walk ABOVE the
        // root: extra ".." segments are absorbed, never turned into a relative path.
        Assert.Equal(expected, FeaturePath.Normalize(path));

    [Fact]
    public void Normalizing_is_idempotent()
    {
        // Targets() normalises defensively, so applying this twice must not change the answer.
        var once = FeaturePath.Normalize("/reports/../admin/?tab=1");
        Assert.Equal(once, FeaturePath.Normalize(once));
    }
}
