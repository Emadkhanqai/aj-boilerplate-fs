using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Tests;

public sealed class InsecureUpstreamTests
{
    private const string TokenEndpointMessage =
        "Auth:Keycloak:TokenEndpointUrl is http. Plain http to an upstream that receives a client secret is refused unless Auth:AllowInsecureUpstreams=true or the environment is Development.";

    private static AuthOptions WithHttpTokenEndpoint()
    {
        var options = TestConfig.Options();
        options.Keycloak.TokenEndpointUrl = "http://keycloak.internal/realms/x/protocol/openid-connect/token";
        return options;
    }

    [Fact]
    public void The_test_configuration_is_valid() =>
        Assert.True(new AuthOptionsValidator().Validate(null, TestConfig.Options()).Succeeded);

    [Fact]
    public void An_http_token_endpoint_is_refused_without_an_opt_in()
    {
        var result = new AuthOptionsValidator().Validate(null, WithHttpTokenEndpoint());

        Assert.True(result.Failed);
        Assert.Contains(TokenEndpointMessage, result.Failures!, StringComparer.Ordinal);
    }

    [Fact]
    public void The_explicit_opt_in_admits_it()
    {
        var options = WithHttpTokenEndpoint();
        options.AllowInsecureUpstreams = true;
        Assert.True(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_development_host_admits_it_without_the_opt_in() =>
        Assert.True(new AuthOptionsValidator(isDevelopment: true).Validate(null, WithHttpTokenEndpoint()).Succeeded);

    [Fact]
    public void An_https_token_endpoint_needs_no_opt_in()
    {
        var options = TestConfig.Options();
        options.Keycloak.TokenEndpointUrl = "https://keycloak.example/realms/x/protocol/openid-connect/token";
        Assert.True(new AuthOptionsValidator().Validate(null, options).Succeeded);
    }
}
