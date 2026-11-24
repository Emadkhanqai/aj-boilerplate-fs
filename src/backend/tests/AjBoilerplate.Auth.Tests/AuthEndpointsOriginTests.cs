using Microsoft.AspNetCore.Http;
using AjBoilerplate.Auth.Endpoints;

namespace AjBoilerplate.Auth.Tests;

public sealed class AuthEndpointsOriginTests
{
    private const string Origin = TestConfig.Origin;

    private static HttpRequest Request(string? origin = null, string? fetchSite = null)
    {
        var context = new DefaultHttpContext();
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        return context.Request;
    }

    [Fact]
    public void A_same_origin_form_post_is_accepted()
    {
        Assert.True(IsSameOriginFor(Request(origin: Origin, fetchSite: "same-origin")));
    }

    [Theory]
    [InlineData("https://app.example.com", null)]
    [InlineData("HTTPS://APP.EXAMPLE.COM", null)]
    [InlineData(null, "same-origin")]
    public void Either_signal_alone_is_enough(string? origin, string? fetchSite) =>
        Assert.True(IsSameOriginFor(Request(origin, fetchSite)));

    [Theory]
    [InlineData("https://evil.example", null)]
    [InlineData("http://app.example.com", null)]
    [InlineData("https://app.example.com.evil.example", null)]
    [InlineData("https://evil.example", "same-origin")]
    [InlineData(null, "cross-site")]
    [InlineData(null, "same-site")]
    [InlineData(null, null)]
    public void Everything_else_is_refused(string? origin, string? fetchSite) =>
        Assert.False(IsSameOriginFor(Request(origin, fetchSite)));

    private static bool IsSameOriginFor(HttpRequest request) => AuthEndpoints.IsSameOrigin(request, Origin);
}
