using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Identity;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// The REAL bearer pipeline — JwtBearer, the JWKS-backed key resolver, the azp check, the claims
/// transformation and the policies — with tokens signed by a throwaway key and the JWKS served from
/// memory. Every other integration test swaps authentication for <see cref="TestAuthHandler"/>, which
/// proves the policies and says nothing about whether a real token is accepted or refused.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class KeycloakGatewayPipelineTests : IDisposable
{
    private const string Issuer = "https://keycloak.internal/realms/test";
    private const string ClientId = "boilerplate-api";
    private const string JwksUri = "http://keycloak.test/realms/test/protocol/openid-connect/certs";

    private static readonly string[] EnvironmentKeys =
        ["Keycloak__ValidIssuer", "Keycloak__ClientId", "Keycloak__JwksUri", "Keycloak__HonourRealmRoles"];

    private readonly RsaSecurityKey _signingKey;
    private readonly RsaSecurityKey _otherKey;
    private readonly WebApplicationFactory<Program> _factory;

    // The factory reads configuration from environment variables for the reason ApiFactory documents:
    // Program.cs binds Keycloak options during service registration, before a test host's own
    // configuration callbacks apply. They are cleared in Dispose so no other test host inherits them.
    public KeycloakGatewayPipelineTests(SqlServerFixture fixture)
    {
        _ = fixture;
        _signingKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "key-1" };
        _otherKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "key-1" };

        Environment.SetEnvironmentVariable("Keycloak__ValidIssuer", Issuer);
        Environment.SetEnvironmentVariable("Keycloak__ClientId", ClientId);
        Environment.SetEnvironmentVariable("Keycloak__JwksUri", JwksUri);

        var jwks = JwksJson(_signingKey);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(nameof(AjBoilerplate.Api.Identity.KeycloakSigningKeyProvider))
                    .ConfigurePrimaryHttpMessageHandler(() => new FixedResponseHandler(jwks)));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var key in EnvironmentKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task A_token_for_this_client_with_a_seat_is_accepted_and_the_seat_is_reported()
    {
        var token = Mint(roles: ["editor"]);

        var response = await GetAsync("/api/v1/me", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadEnvelopeAsync<UserProfileResponse>();
        Assert.Equal([ApplicationRoles.Editor], profile.Roles);
        Assert.True(profile.Capabilities.CanEdit);
        Assert.Empty(profile.Permissions);
    }

    [Fact]
    public async Task A_token_carrying_only_a_permission_is_accepted_as_a_permission_and_not_as_a_seat()
    {
        var token = Mint(roles: ["items:read"]);

        var profile = await (await GetAsync("/api/v1/me", token)).Content.ReadEnvelopeAsync<UserProfileResponse>();

        Assert.Empty(profile.Roles);
        Assert.Equal([Permissions.ItemsRead], profile.Permissions);
        Assert.True(profile.Capabilities.CanView);
        Assert.False(profile.Capabilities.CanCreate);
    }

    [Fact]
    public async Task A_token_issued_to_a_different_client_is_a_401_even_though_issuer_and_signature_are_valid() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/v1/me", Mint(azp: "someone-elses-client", roles: ["admin"]))).StatusCode);

    [Fact]
    public async Task A_token_with_no_azp_is_a_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/v1/me", Mint(azp: null, roles: ["admin"]))).StatusCode);

    [Fact]
    public async Task A_token_from_another_issuer_is_a_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/v1/me", Mint(issuer: "https://evil.example/realms/test", roles: ["admin"]))).StatusCode);

    [Fact]
    public async Task A_token_signed_by_a_key_the_realm_does_not_publish_is_a_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/v1/me", Mint(key: _otherKey, roles: ["admin"]))).StatusCode);

    [Fact]
    public async Task An_expired_token_is_a_401_with_no_clock_skew_allowance() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/v1/me", Mint(expiresIn: TimeSpan.FromSeconds(-2), roles: ["admin"]))).StatusCode);

    [Fact]
    public async Task No_token_at_all_is_a_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/me", CancellationToken.None)).StatusCode);

    [Fact]
    public async Task A_realm_wide_role_is_not_a_seat_by_default()
    {
        // A shared realm's realm-level "admin" belongs to another application. Only this client's own
        // roles count unless the deployment opts in.
        var token = Mint(realmRoles: ["admin"]);

        var profile = await (await GetAsync("/api/v1/me", token)).Content.ReadEnvelopeAsync<UserProfileResponse>();

        Assert.Empty(profile.Roles);
        Assert.False(profile.Capabilities.CanAdminister);
    }

    [Fact]
    public async Task A_valid_token_with_no_seat_and_no_permission_is_a_403_on_a_protected_route() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/api/v1/items", Mint(roles: ["something-else"]))).StatusCode);

    [Fact]
    public async Task The_health_probes_stay_anonymous_under_the_deny_by_default_fallback() =>
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/health/live", CancellationToken.None)).StatusCode);

    private Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync(path, CancellationToken.None);
    }

    private string Mint(
        string? azp = ClientId,
        string issuer = Issuer,
        RsaSecurityKey? key = null,
        TimeSpan? expiresIn = null,
        string[]? roles = null,
        string[]? realmRoles = null)
    {
        var now = DateTime.UtcNow;
        var expires = now + (expiresIn ?? TimeSpan.FromMinutes(5));
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "oid-pipeline",
            ["name"] = "Pipeline User",
            ["email"] = "pipeline@example.com",
        };

        if (azp is not null)
        {
            claims["azp"] = azp;
        }

        if (roles is not null)
        {
            claims["resource_access"] = new Dictionary<string, object>
            {
                [ClientId] = new Dictionary<string, object> { ["roles"] = roles },
            };
        }

        if (realmRoles is not null)
        {
            claims["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles };
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            IssuedAt = now.AddMinutes(-10),
            NotBefore = now.AddMinutes(-10),
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? _signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static string JwksJson(RsaSecurityKey key)
    {
        var p = key.Rsa.ExportParameters(false);
        return $$"""{"keys":[{"kty":"RSA","kid":"{{key.KeyId}}","use":"sig","alg":"RS256","n":"{{Base64UrlEncoder.Encode(p.Modulus)}}","e":"{{Base64UrlEncoder.Encode(p.Exponent)}}"}]}""";
    }

    private sealed class FixedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
