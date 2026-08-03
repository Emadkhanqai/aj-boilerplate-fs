using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.IntegrationTests.Support;

/// <summary>
/// Authenticates a request from an <c>X-Test-Role</c> header, standing in for the real
/// Keycloak/cloud-IdP pipeline so authorization can be exercised without a live identity provider.
///
/// No header means NO authenticated principal, so protected endpoints answer 401 through the real
/// pipeline — the anonymous path is genuinely tested, not bypassed. The header value is a role name
/// and is emitted as a <see cref="ClaimTypes.Role"/> claim, exactly as the Keycloak transformation
/// would. An optional <c>X-Test-Group</c> header (comma-separated) emits one <c>groups</c> claim per
/// value, and an optional <c>X-Test-User</c> header overrides the subject identifier — needed by any
/// test that must prove two DIFFERENT people are treated independently.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string RoleHeader = "X-Test-Role";
    public const string GroupHeader = "X-Test-Group";

    /// <summary>Overrides the subject identifier the request authenticates as.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>The subject used when <see cref="UserHeader"/> is absent.</summary>
    public const string DefaultUserId = "test-oid";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RoleHeader, out var role) || string.IsNullOrWhiteSpace(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var userId = Request.Headers.TryGetValue(UserHeader, out var user) && !string.IsNullOrWhiteSpace(user)
            ? user.ToString()
            : DefaultUserId;

        var claims = new List<Claim>
        {
            new("oid", userId),
            new("name", "Test User"),
            new(ClaimTypes.Role, role.ToString()),
        };

        if (Request.Headers.TryGetValue(GroupHeader, out var groups))
        {
            claims.AddRange(groups.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(g => new Claim("groups", g)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "name", ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
