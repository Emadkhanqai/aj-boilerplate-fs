using System.Security.Claims;
using AjBoilerplate.Api.Identity;
using AjBoilerplate.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AjBoilerplate.UnitTests.Identity;

/// <summary>
/// The composition rules of <see cref="AuthenticationSetup"/>: what refuses to start, and what the
/// three coarse policies decide for a seat, a permission, both and neither. The policies are driven
/// through the real <see cref="IAuthorizationService"/> and the real claims transformation, so a
/// raw token-shaped principal goes in and an authorization decision comes out.
/// </summary>
public sealed class AuthenticationSetupTests
{
    private const string Https = "https://keycloak.example.com/realms/r/protocol/openid-connect/certs";
    private const string Http = "http://keycloak.internal/realms/r/protocol/openid-connect/certs";

    [Fact]
    public void A_plaintext_JWKS_refuses_to_start_outside_Development() =>
        Assert.Contains("not an https URL", Assert.Throws<InvalidOperationException>(
            () => Compose(Production, Keycloak(jwks: Http))).Message);

    [Fact]
    public void A_plaintext_JWKS_is_allowed_in_Development() =>
        Compose(Development, Keycloak(jwks: Http));

    [Fact]
    public void An_https_JWKS_starts_outside_Development() =>
        Compose(Production, Keycloak(jwks: Https));

    [Fact]
    public void An_empty_JWKS_refuses_to_start_outside_Development_unless_discovery_is_opted_into()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => Compose(Production, Keycloak(jwks: "", authority: "https://keycloak.example.com/realms/r")));
        Assert.Contains(KeycloakOptions.AllowDiscoveryFallbackKey, failure.Message);

        Compose(Production, Keycloak(jwks: "", authority: "https://keycloak.example.com/realms/r", allowDiscovery: true));
        Compose(Development, Keycloak(jwks: "", authority: "https://keycloak.example.com/realms/r"));
    }

    [Fact]
    public void Nothing_configured_starts_anywhere_and_validates_no_token() =>
        // The "nothing configured => every protected endpoint 401" behaviour: no Keycloak, no scheme
        // that could accept anything, and no startup failure.
        Compose(Production, []);

    [Fact]
    public void An_unrecognised_auth_mode_refuses_to_start_and_blank_or_gateway_does_not()
    {
        Assert.Contains("not a recognised mode", Assert.Throws<InvalidOperationException>(
            () => Compose(Production, new Dictionary<string, string?> { ["Auth:Mode"] = "none" })).Message);

        Compose(Production, new Dictionary<string, string?> { ["Auth:Mode"] = "Gateway" });
        Compose(Production, new Dictionary<string, string?> { ["Auth:Mode"] = "" });
    }

    [Fact]
    public async Task The_fallback_policy_denies_an_endpoint_that_declares_nothing()
    {
        var policy = await Services().GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    // seat / permission / both / neither, for each coarse policy.
    [Theory]
    [InlineData(Policies.ReadAccess, "viewer", true)]
    [InlineData(Policies.ReadAccess, "editor", true)]
    [InlineData(Policies.ReadAccess, "admin", true)]
    [InlineData(Policies.ReadAccess, "items:read", true)]
    [InlineData(Policies.ReadAccess, "items:write", false)]
    [InlineData(Policies.ReadAccess, "admin:manage", false)]
    [InlineData(Policies.WriteAccess, "viewer", false)]
    [InlineData(Policies.WriteAccess, "editor", true)]
    [InlineData(Policies.WriteAccess, "admin", true)]
    [InlineData(Policies.WriteAccess, "items:write", true)]
    [InlineData(Policies.WriteAccess, "items:read", false)]
    [InlineData(Policies.AdminAccess, "editor", false)]
    [InlineData(Policies.AdminAccess, "admin", true)]
    [InlineData(Policies.AdminAccess, "admin:manage", true)]
    [InlineData(Policies.AdminAccess, "items:write", false)]
    public async Task A_policy_is_satisfied_by_the_seat_hierarchy_or_the_matching_permission(
        string policy, string held, bool allowed) =>
        Assert.Equal(allowed, await IsAllowedAsync(policy, held));

    [Theory]
    [InlineData(Policies.ReadAccess)]
    [InlineData(Policies.WriteAccess)]
    [InlineData(Policies.AdminAccess)]
    public async Task A_caller_with_neither_a_seat_nor_a_permission_is_refused_by_every_policy(string policy)
    {
        Assert.False(await IsAllowedAsync(policy));
        Assert.False(await IsAllowedAsync(policy, "superuser", "items:delete", "administrator", "items"));
    }

    [Fact]
    public async Task A_seat_and_a_permission_together_are_granted_once_and_do_not_narrow_each_other()
    {
        Assert.True(await IsAllowedAsync(Policies.WriteAccess, "viewer", "items:write"));
        Assert.False(await IsAllowedAsync(Policies.AdminAccess, "viewer", "items:write"));
    }

    [Fact]
    public async Task An_unauthenticated_principal_is_refused_even_if_it_carries_the_claims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")]));

        var result = await Services().GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, null, Policies.ReadAccess);

        Assert.False(result.Succeeded);
    }

    private static async Task<bool> IsAllowedAsync(string policy, params string[] rawKeys)
    {
        var provider = Services();
        var raw = new ClaimsPrincipal(new ClaimsIdentity(rawKeys.Select(k => new Claim(ClaimTypes.Role, k)), "TestBearer"));
        var principal = await provider.GetRequiredService<IClaimsTransformation>().TransformAsync(raw);

        return (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, policy)).Succeeded;
    }

    private static ServiceProvider Services() => Compose(Development, Keycloak(jwks: Https));

    private static ServiceProvider Compose(IHostEnvironment environment, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiAuthentication(configuration, environment);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Keycloak(
        string jwks, string authority = "", bool allowDiscovery = false) => new()
        {
            ["Keycloak:ValidIssuer"] = "https://keycloak.internal/realms/r",
            ["Keycloak:ClientId"] = "my-api",
            ["Keycloak:JwksUri"] = jwks,
            ["Keycloak:Authority"] = authority,
            ["Keycloak:AllowDiscoveryFallback"] = allowDiscovery.ToString(),
        };

    private static readonly IHostEnvironment Production = new FakeEnvironment(Environments.Production);

    private static readonly IHostEnvironment Development = new FakeEnvironment(Environments.Development);

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string name) => EnvironmentName = name;

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
