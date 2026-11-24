using System.Text;
using System.Text.Json;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class RedirectTargetTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/budgets/FY26-4412")]
    [InlineData("/search?q=a%20b#top")]
    public void Local_paths_are_safe(string value) => Assert.True(RedirectTarget.IsSafe(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("budgets")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/ok\\..\\x")]
    [InlineData("https://evil.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/a\nb")]
    public void Anything_else_is_refused(string? value) => Assert.False(RedirectTarget.IsSafe(value));

    [Fact]
    public void Unsafe_values_fall_back_to_root() => Assert.Equal("/", RedirectTarget.OrDefault("//evil.example"));
}

public sealed class SessionIdsTests
{
    [Fact]
    public void Random_ids_are_256_bit_and_well_formed()
    {
        var id = SessionIds.NewRandom();
        Assert.Equal(43, id.Length);
        Assert.True(SessionIds.IsWellFormed(id));
        Assert.NotEqual(id, SessionIds.NewRandom());
    }

    [Fact]
    public void Storage_key_is_not_the_cookie_value() =>
        Assert.NotEqual("abc", SessionIds.StorageKey("abc"));

    [Fact]
    public void Pkce_challenge_matches_rfc7636_appendix_b() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", SessionIds.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
}

public sealed class JwtClaimsTests
{
    private static readonly DateTimeOffset Exp = new(2026, 9, 22, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_sub_exp_and_only_this_clients_roles()
    {
        var claims = JwtClaims.TryParse(Jwt.Create("kc-user", Exp, "ebudgeting", "requester", "ba_manager"))!;

        Assert.Equal("kc-user", claims.Subject);
        Assert.Equal(Exp, claims.ExpiresAt);
        Assert.Equal(new[] { "requester", "ba_manager" }, claims.ClientRoles("ebudgeting"));
        Assert.Empty(claims.ClientRoles("cad"));
    }

    [Fact]
    public void Flat_roles_are_read_when_azp_names_this_client()
    {
        var claims = JwtClaims.TryParse(Jwt.CreateFlat("kc-user", Exp, "ebudgeting", "requester", "budgets:edit"))!;

        Assert.Equal("ebudgeting", claims.AuthorizedParty);
        Assert.False(claims.IsForAnotherClient("ebudgeting"));
        Assert.Equal(new[] { "requester", "budgets:edit" }, claims.ClientRoles("ebudgeting"));
    }

    [Fact]
    public void Flat_roles_are_ignored_when_azp_names_another_client()
    {
        var claims = JwtClaims.TryParse(Jwt.CreateFlat("kc-user", Exp, "cad", "requester", "budgets:edit"))!;

        Assert.True(claims.IsForAnotherClient("ebudgeting"));
        Assert.Empty(claims.ClientRoles("ebudgeting"));
    }

    [Fact]
    public void Flat_roles_are_ignored_when_the_token_names_no_azp()
    {
        var claims = JwtClaims.TryParse(Jwt.CreateFlat("kc-user", Exp, azp: null, "requester"))!;

        Assert.False(claims.IsForAnotherClient("ebudgeting"));
        Assert.Empty(claims.ClientRoles("ebudgeting"));
    }

    [Fact]
    public void Neither_role_claim_present_is_no_roles()
    {
        var claims = JwtClaims.TryParse(Jwt.CreateFlat("kc-user", Exp, "ebudgeting"))!;

        Assert.Empty(claims.ClientRoles("ebudgeting"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.!!!.c")]
    [InlineData("eyJhbGciOiJub25lIn0.e30.x")] // {} — no sub/exp
    public void Garbage_is_null(string? jwt) => Assert.Null(JwtClaims.TryParse(jwt));

    [Theory]
    [InlineData("""{"sub":"kc-user","exp":"1758542700"}""")]          // exp as a string
    [InlineData("""{"sub":"kc-user","exp":true}""")]                   // exp as a bool
    [InlineData("""{"sub":"kc-user","exp":null}""")]                   // exp as null
    [InlineData("""{"sub":"kc-user","exp":{"v":1758542700}}""")]       // exp as an object
    [InlineData("""{"sub":123,"exp":1758542700}""")]                   // sub as a number
    [InlineData("""{"sub":["kc-user"],"exp":1758542700}""")]           // sub as an array
    [InlineData("""["not","an","object"]""")]                          // payload not an object
    public void Wrong_typed_claims_are_refused_not_thrown(string payloadJson) =>
        Assert.Null(JwtClaims.TryParse(Jwt.CreateRaw(payloadJson)));

    [Theory]
    [InlineData("""{"sub":"kc-user","exp":1758542700,"azp":7,"roles":["requester"]}""")]
    [InlineData("""{"sub":"kc-user","exp":1758542700,"azp":"ebudgeting","roles":"requester"}""")]
    [InlineData("""{"sub":"kc-user","exp":1758542700,"azp":"ebudgeting","roles":[1,null,{}]}""")]
    [InlineData("""{"sub":"kc-user","exp":1758542700,"resource_access":"ebudgeting"}""")]
    [InlineData("""{"sub":"kc-user","exp":1758542700,"resource_access":{"ebudgeting":"requester"}}""")]
    [InlineData("""{"sub":"kc-user","exp":1758542700,"resource_access":{"ebudgeting":{"roles":"requester"}}}""")]
    public void Wrong_typed_role_claims_are_no_roles_not_a_throw(string payloadJson)
    {
        var claims = JwtClaims.TryParse(Jwt.CreateRaw(payloadJson))!;

        Assert.NotNull(claims);
        Assert.Empty(claims.ClientRoles("ebudgeting"));
    }
}

public sealed class AuthOptionsValidatorTests
{
    [Fact]
    public void Test_configuration_is_valid() =>
        Assert.True(new AuthOptionsValidator().Validate(null, TestConfig.Options()).Succeeded);

    [Fact]
    public void Missing_offline_access_is_refused()
    {
        var options = TestConfig.Options();
        options.Entra.Scopes = "openid profile";
        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("app.example.com")]
    [InlineData("https://app.example.com/")]
    [InlineData("https://app.example.com/path")]
    [InlineData("http://app.example.com")]
    public void A_public_origin_that_is_not_a_bare_https_origin_is_refused(string origin)
    {
        var options = TestConfig.Options();
        options.PublicOrigin = origin;
        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Plain_http_authority_is_refused()
    {
        var options = TestConfig.Options();
        options.Entra.Authority = "http://tenant.example.com/policy";
        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void An_encryption_key_that_is_not_32_bytes_of_base64_is_refused(string key)
    {
        var options = TestConfig.Options();
        options.Session.EncryptionKey = key;

        var result = new AuthOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Auth:Session:EncryptionKey must be base64 of exactly 32 bytes.", result.Failures!, StringComparer.Ordinal);
    }

    [Fact]
    public void Insecure_cookies_are_refused_outside_development_and_allowed_in_it()
    {
        var options = TestConfig.Options();
        options.Cookies.Secure = false;
        options.PublicOrigin = "http://localhost:4200";

        Assert.Contains("Auth:Cookies:Secure=false is refused outside Development.", new AuthOptionsValidator().Validate(null, options).Failures!, StringComparer.Ordinal);
        Assert.True(new AuthOptionsValidator(isDevelopment: true).Validate(null, options).Succeeded);
    }

    [Fact]
    public void Cookie_names_follow_the_secure_flag()
    {
        var cookies = TestConfig.Options().Cookies;
        Assert.Equal("__Host-app_sid", cookies.SessionName);
        Assert.Equal("__Host-app_login", cookies.LoginName);

        cookies.Secure = false;
        Assert.Equal("app_sid", cookies.SessionName);
        Assert.Equal("app_login", cookies.LoginName);
    }

    [Fact]
    public void A_wait_budget_beyond_the_ceiling_is_refused()
    {
        var options = TestConfig.Options();
        options.Session.RefreshLockLifetime = TimeSpan.FromSeconds(30);
        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Endpoints_and_scopes_follow_the_authority()
    {
        var entra = TestConfig.Options().Entra;

        Assert.Equal("https://tenant.example.com/policy/oauth2/v2.0/token", entra.TokenEndpoint.AbsoluteUri);
        Assert.Equal("openid profile offline_access entra-client", entra.EffectiveScopes);

        entra.Scopes = "openid profile offline_access entra-client";
        Assert.Equal("openid profile offline_access entra-client", entra.EffectiveScopes);
    }
}

public sealed class ForceRefreshHandlerTests
{
    [Fact]
    public async Task Flags_every_live_session_and_prunes_expired_index_entries()
    {
        var store = new InMemorySessionStore();
        var live = new AuthSession("jwt", DateTimeOffset.UtcNow.AddMinutes(5), "rt", "kc-user", TestConfig.ClientId, false, DateTimeOffset.UtcNow);
        store.Sessions["a"] = live;
        store.Sessions["b"] = live with { ClientId = "other" };
        store.Index["kc-user"] = ["a", "b", "expired"];

        var flagged = await new ForceRefreshHandler(store, Log.For<ForceRefreshHandler>()).HandleAsync("kc-user", default);

        Assert.Equal(2, flagged);
        Assert.True(store.Sessions["a"].ForceRefresh);
        Assert.True(store.Sessions["b"].ForceRefresh);
        Assert.DoesNotContain("expired", store.Index["kc-user"]);
    }

    [Fact]
    public async Task Is_idempotent()
    {
        var store = new InMemorySessionStore();
        store.Sessions["a"] = new AuthSession("jwt", DateTimeOffset.UtcNow, "rt", "kc-user", TestConfig.ClientId, false, DateTimeOffset.UtcNow);
        store.Index["kc-user"] = ["a"];
        var sut = new ForceRefreshHandler(store, Log.For<ForceRefreshHandler>());

        await sut.HandleAsync("kc-user", default);
        var second = await sut.HandleAsync("kc-user", default);

        Assert.Equal(1, second);
        Assert.True(store.Sessions["a"].ForceRefresh);
    }
}
