using Microsoft.Extensions.Logging.Abstractions;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class LoginHintTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly FakeEntra _entra = new();
    private readonly SignInService _sut;

    public LoginHintTests()
    {
        var options = TestConfig.Options();
        _sut = new SignInService(
            new InMemorySessionStore(), new InMemoryLoginStore(), _entra, new FakeKeycloak(), TestConfig.Wrap(options), new ManualTime(DateTimeOffset.UnixEpoch), Log.For<SignInService>());
    }

    private string AuthorizeUrl(SignInPrompt prompt, string? hint)
    {
        var options = TestConfig.Options();
        options.Entra.Authority = "https://idp.example/tenant/B2C_1A_GENERIC-SIGNIN";
        options.Entra.ClientId = "client-abc";
        options.Entra.ClientSecret = "not-used-on-this-path";
        var client = new EntraClient(_http, TestConfig.Wrap(options), NullLogger<EntraClient>.Instance);
        return client.BuildAuthorizeUrl(new Uri("https://portal.example/auth/callback"), "state-1", "challenge-1", prompt, hint).AbsoluteUri;
    }

    public void Dispose() => _http.Dispose();

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("  first.last+tag@sub.example.com  ")]
    public void A_valid_email_is_kept_trimmed(string input) =>
        Assert.Equal(input.Trim(), LoginHints.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("a@b@c.com")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@nodot")]
    [InlineData("user@.com")]
    [InlineData("user@example.")]
    [InlineData("us er@example.com")]
    [InlineData("user@example.com\nprompt=none")]
    [InlineData("user@example.com\u0000")]
    [InlineData("a@b.com,c@d.com")]
    [InlineData("<script>@x.com")]
    public void An_invalid_hint_becomes_null(string? input) =>
        Assert.Null(LoginHints.Normalize(input));

    [Fact]
    public void The_length_limit_is_254()
    {
        var domain = "@example.com";
        Assert.NotNull(LoginHints.Normalize(new string('a', LoginHints.MaxLength - domain.Length) + domain));
        Assert.Null(LoginHints.Normalize(new string('a', LoginHints.MaxLength - domain.Length + 1) + domain));
    }

    [Fact]
    public void No_hint_puts_no_login_hint_on_the_authorize_url()
    {
        var url = AuthorizeUrl(SignInPrompt.None, null);

        Assert.DoesNotContain("login_hint", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("code_challenge_method=S256", url, StringComparison.Ordinal);   // control: a real URL
    }

    [Fact]
    public void A_hint_is_percent_encoded_and_cannot_add_a_parameter()
    {
        var url = AuthorizeUrl(SignInPrompt.None, "a+b&prompt=none@example.com");

        Assert.Contains("&login_hint=a%2Bb%26prompt%3Dnone@example.com", url, StringComparison.Ordinal);
        Assert.DoesNotContain("&prompt=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hint_combines_with_prompt_login()
    {
        var url = AuthorizeUrl(SignInPrompt.Login, "user@example.com");

        Assert.Contains("prompt=login", url, StringComparison.Ordinal);
        Assert.Contains("login_hint=user@example.com", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not an email")]
    [InlineData("user@example.com\r\nSet-Cookie: x=1")]
    public async Task Begin_drops_an_invalid_hint_and_still_redirects(string? hint)
    {
        var start = await _sut.BeginAsync("/", SignInPrompt.None, default, hint);

        Assert.NotNull(start.AuthorizeUrl);
        Assert.Null(_entra.LastLoginHint);
        Assert.NotNull(_entra.LastPrompt);   // control: BuildAuthorizeUrl was reached on this call
    }

    [Fact]
    public async Task Begin_forwards_a_valid_hint_trimmed()
    {
        var start = await _sut.BeginAsync("/", SignInPrompt.None, default, " user@example.com ");

        Assert.NotNull(start.AuthorizeUrl);
        Assert.Equal("user@example.com", _entra.LastLoginHint);
    }
}
