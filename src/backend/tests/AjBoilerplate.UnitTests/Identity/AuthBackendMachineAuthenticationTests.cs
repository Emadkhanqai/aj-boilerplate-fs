using System.Security.Claims;
using AjBoilerplate.Api.Identity;
using Microsoft.IdentityModel.Tokens;

namespace AjBoilerplate.UnitTests.Identity;

public sealed class AuthBackendMachineAuthenticationTests
{
    [Theory]
    [InlineData("the-key", "the-key", true)]
    [InlineData("the-keyX", "the-key", false)]
    [InlineData("the-ke", "the-key", false)]
    [InlineData("THE-KEY", "the-key", false)]
    [InlineData("", "the-key", false)]
    public void The_events_key_must_match_exactly(string supplied, string expected, bool matches) =>
        Assert.Equal(matches, AuthBackendEventsKeyHandler.KeyMatches(supplied, expected));

    [Fact]
    public void The_surface_exists_only_once_a_key_or_a_google_caller_is_configured()
    {
        Assert.False(new AuthBackendIntegrationOptions().IsEnabled);
        Assert.True(new AuthBackendIntegrationOptions { EventsKey = "k" }.IsEnabled);
        Assert.True(new AuthBackendIntegrationOptions { Audience = "https://api.example", AllowedCallerEmail = "sa@x.iam" }.IsEnabled);

        // Half of the Google pair is not a configuration: either alone would accept a stranger or nobody.
        Assert.False(new AuthBackendIntegrationOptions { Audience = "https://api.example" }.IsEnabled);
        Assert.False(new AuthBackendIntegrationOptions { AllowedCallerEmail = "sa@x.iam" }.IsEnabled);
        Assert.False(new AuthBackendIntegrationOptions { EventsKey = "  " }.IsEnabled);
    }

    [Fact]
    public void Both_run_app_origins_can_be_listed()
    {
        var options = new AuthBackendIntegrationOptions { Audience = "https://a.run.app, https://b.run.app;" };

        Assert.Equal(["https://a.run.app", "https://b.run.app"], options.Audiences);
    }

    [Theory]
    [InlineData("true", "sa@x.iam", "SA@x.iam", null)]
    [InlineData("false", "sa@x.iam", "sa@x.iam", "email-not-verified")]
    [InlineData(null, "sa@x.iam", "sa@x.iam", "email-not-verified")]
    [InlineData("true", "other@x.iam", "sa@x.iam", "caller-not-allowed")]
    [InlineData("true", null, "sa@x.iam", "caller-not-allowed")]
    public void A_google_token_must_come_from_the_one_verified_caller(string? verified, string? email, string allowed, string? expectedProblem)
    {
        var claims = new List<Claim>();
        if (verified is not null)
        {
            claims.Add(new Claim("email_verified", verified));
        }

        if (email is not null)
        {
            claims.Add(new Claim("email", email));
        }

        Assert.Equal(expectedProblem, AuthBackendGoogleIdTokenScheme.CallerProblem(new ClaimsPrincipal(new ClaimsIdentity(claims)), allowed));
    }

    [Fact]
    public void A_rejection_reason_is_derived_from_the_exception_type_and_never_its_message()
    {
        Assert.Equal("token-expired", AuthBackendGoogleIdTokenScheme.ReasonSlug(new SecurityTokenExpiredException("secret claim value")));
        Assert.Equal("audience-mismatch", AuthBackendGoogleIdTokenScheme.ReasonSlug(new SecurityTokenInvalidAudienceException("https://leak")));
        Assert.Equal("signing-keys-unavailable", AuthBackendGoogleIdTokenScheme.ReasonSlug(new HttpRequestException("egress")));
        Assert.Equal("token-invalid", AuthBackendGoogleIdTokenScheme.ReasonSlug(new ArgumentException("anything")));
    }
}
