using Microsoft.Extensions.Logging.Abstractions;
using AjBoilerplate.Auth.Identity;

namespace AjBoilerplate.Auth.Tests;

public sealed class SignInPromptTests
{
    private static EntraClient Client()
    {
        var options = TestConfig.Options();
        options.Entra.Authority = "https://idp.example/tenant/B2C_1A_GENERIC-SIGNIN";
        options.Entra.ClientId = "client-abc";
        options.Entra.ClientSecret = "not-used-on-this-path";
        return new EntraClient(new HttpClient(), TestConfig.Wrap(options), NullLogger<EntraClient>.Instance);
    }

    private static string AuthorizeUrl(SignInPrompt prompt) =>
        Client().BuildAuthorizeUrl(new Uri("https://portal.example/auth/callback"), "state-1", "challenge-1", prompt).AbsoluteUri;

    [Theory]
    [InlineData("login", SignInPrompt.Login)]
    [InlineData("select_account", SignInPrompt.SelectAccount)]
    public void Parse_accepts_the_two_allowed_values(string requested, SignInPrompt expected) =>
        Assert.Equal(expected, SignInPrompts.Parse(requested));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Login")]
    [InlineData("LOGIN")]
    [InlineData(" login")]
    [InlineData("login ")]
    [InlineData("none")]
    [InlineData("consent")]
    [InlineData("login select_account")]
    [InlineData("login&redirect_uri=https://evil.example/")]
    [InlineData("../../evil")]
    public void Parse_drops_everything_else(string? requested) =>
        Assert.Equal(SignInPrompt.None, SignInPrompts.Parse(requested));

    [Fact]
    public void None_puts_no_prompt_parameter_on_the_authorize_url()
    {
        var url = AuthorizeUrl(SignInPrompt.None);

        Assert.DoesNotContain("prompt", url, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("code_challenge_method=S256", url, StringComparison.Ordinal);
        Assert.Contains("client_id=client-abc", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignInPrompt.Login, "prompt=login")]
    [InlineData(SignInPrompt.SelectAccount, "prompt=select_account")]
    public void An_allowed_prompt_reaches_the_authorize_url(SignInPrompt prompt, string expected)
    {
        var url = AuthorizeUrl(prompt);

        Assert.Contains(expected, url, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hostile_prompt_value_cannot_inject_a_parameter()
    {
        var url = AuthorizeUrl(SignInPrompts.Parse("login&redirect_uri=https%3A%2F%2Fevil.example%2F"));

        Assert.DoesNotContain("evil.example", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redirect_uri=https%3A%2F%2Fportal.example%2Fauth%2Fcallback", url, StringComparison.Ordinal);
    }
}
