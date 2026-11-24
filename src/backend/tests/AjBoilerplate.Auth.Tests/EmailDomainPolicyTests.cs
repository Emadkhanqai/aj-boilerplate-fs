using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Events;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// A NEW directory account only in an allowed e-mail domain, deny by default, refused before the directory is called.
/// </summary>
public sealed class EmailDomainPolicyTests
{
    private static readonly string[] Allowed = ["example.com"];

    [Theory]
    [InlineData("r4@maroon.vip")]                    // the ticket's address
    [InlineData("someone@evil-example.com")]       // a look-alike prefix
    [InlineData("someone@example.com.evil.com")]   // the allowed name as a subdomain of another
    [InlineData("someone@sub.example.com")]        // a subdomain that is not itself listed
    [InlineData("someone@alj\u0430zeera.net")]            // Cyrillic '\u0430' (U+0430): becomes xn--..., matches nothing
    [InlineData("a@b@example.com")]                // two @
    [InlineData("someone @example.com")]           // whitespace
    [InlineData("some\tone@example.com")]          // control character
    [InlineData("@example.com")]                   // no local part
    [InlineData("someone@")]                         // no domain
    [InlineData("someone@example")]                // not a host name with a dot
    [InlineData("")]
    [InlineData(null)]
    public void An_address_outside_the_allowlist_is_refused(string? email) =>
        Assert.NotEqual(EmailDomainVerdict.Allowed, EmailDomainPolicy.Check(email, Allowed));

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("Someone@EXAMPLE.COM")]            // case
    [InlineData("someone@example.com.")]           // the trailing root dot
    [InlineData("someone@example.com..")]          // TrimEnd('.') strips every trailing dot; still the same host
    [InlineData("  someone+tag@example.com ")]     // surrounding space and plus-addressing
    public void An_address_in_an_allowed_domain_is_admitted(string email) =>
        Assert.Equal(EmailDomainVerdict.Allowed, EmailDomainPolicy.Check(email, Allowed));

    [Fact]
    public void Verdicts_are_distinguished_and_the_list_is_normalised_like_the_address()
    {
        Assert.Equal(EmailDomainVerdict.Allowed, EmailDomainPolicy.Check("x@example.com", [" EXAMPLE.com. "]));
        Assert.Equal(EmailDomainVerdict.Allowed, EmailDomainPolicy.Check("x@bücher.example", ["xn--bcher-kva.example"]));
        Assert.Equal(EmailDomainVerdict.NotConfigured, EmailDomainPolicy.Check("x@example.com", ["", "  ", "nodot"]));
        Assert.Equal(EmailDomainVerdict.NotConfigured, EmailDomainPolicy.Check("x@example.com", null));
        Assert.Equal(EmailDomainVerdict.InvalidFormat, EmailDomainPolicy.Check("not-an-address", Allowed));
        Assert.Equal(EmailDomainVerdict.DomainNotAllowed, EmailDomainPolicy.Check("x@other.org", Allowed));
        // A second listed domain (dev lists two) is honoured, and still exact.
        Assert.Equal(EmailDomainVerdict.Allowed, EmailDomainPolicy.Check("x@yopmail.net", ["example.com", "yopmail.net"]));
        Assert.Equal(EmailDomainVerdict.DomainNotAllowed, EmailDomainPolicy.Check("x@mail.yopmail.net", ["example.com", "yopmail.net"]));
    }
}

public sealed class UserCreationDomainServiceTests
{
    private readonly FakeDirectoryAdmin _directory = new();
    private readonly CapturingLogger<UserAdminService> _logger = new();

    private UserAdminService Service(params string[] allowed)
    {
        var options = new AuthOptions();
        foreach (var domain in allowed)
        {
            options.Admin.AllowedEmailDomains.Add(domain);
        }

        var sessions = new InMemorySessionStore();
        return new UserAdminService(
            _directory, sessions, new ForceRefreshHandler(sessions, NullLogger<ForceRefreshHandler>.Instance), TestConfig.Wrap(options), _logger);
    }

    private static NewUser Person(string email) => new(email, "T", "T", "/my-app/editors");

    [Theory]
    [InlineData("r4@maroon.vip")]
    [InlineData("someone@example.com.evil.com")]
    [InlineData("someone@alj\u0430zeera.net")]
    [InlineData("a@b@example.com")]
    [InlineData("")]
    public async Task A_refused_address_never_reaches_the_directory(string email)
    {
        var result = await Service("example.com").CreateUserAsync(Person(email), CancellationToken.None);

        Assert.Equal(AdminOutcome.EmailDomainNotAllowed, result.Outcome);
        Assert.Equal(0, _directory.CreateCalls);
    }

    [Fact]
    public async Task An_address_in_an_allowed_domain_reaches_the_directory()
    {
        var result = await Service("example.com").CreateUserAsync(Person("Someone@EXAMPLE.com"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, _directory.CreateCalls);
    }

    [Fact]
    public async Task An_empty_allowlist_refuses_everything_and_logs_an_error_naming_the_setting()
    {
        var result = await Service().CreateUserAsync(Person("someone@example.com"), CancellationToken.None);

        Assert.Equal(AdminOutcome.EmailDomainNotAllowed, result.Outcome);
        Assert.Equal(0, _directory.CreateCalls);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Auth:Admin:AllowedEmailDomains", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_carries_the_verdict_and_the_surface_and_never_the_address()
    {
        await Service("example.com").CreateUserAsync(Person("secret.person@maroon.vip"), CancellationToken.None);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("DomainNotAllowed", entry.Fields["Verdict"]);
        Assert.Equal("POST /internal/admin/users", entry.Fields["Surface"]);
        Assert.DoesNotContain("secret.person", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("maroon", entry.Message, StringComparison.Ordinal);
        Assert.All(entry.Fields.Values, v => Assert.DoesNotContain("maroon", v?.ToString() ?? string.Empty, StringComparison.Ordinal));
    }
}

/// <summary>The real <c>/internal/admin/users</c> route over HTTP: 422, a neutral body, directory untouched.</summary>
[Trait("Category", "Integration")]
public sealed class UserCreationDomainEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Audience = "https://auth-backend.test";
    private const string Caller = "admin-console@example.iam.gserviceaccount.com";

    private readonly SigningKeys _keys = new();
    private readonly FakeDirectoryAdmin _directory = new();
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var options = new AuthOptions();
        options.Admin.Audience = Audience;
        options.Admin.AllowedCallers.Add(Caller);
        options.Admin.AllowedEmailDomains.Add("example.com");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TestConfig.Wrap(options));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_keys.Factory());
        builder.Services.AddSingleton<GoogleIdTokenVerifier>();
        builder.Services.AddSingleton<IDirectoryAdmin>(_directory);
        var sessions = new InMemorySessionStore();
        builder.Services.AddSingleton<ISessionStore>(sessions);
        builder.Services.AddScoped<ForceRefreshHandler>();
        builder.Services.AddScoped<UserAdminService>();

        _app = builder.Build();
        _app.MapAdminEndpoints();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
        _keys.Dispose();
    }

    private static string Segment(object value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private async Task<HttpResponseMessage> PostAsync(string email)
    {
        var token = _keys.Sign(
            Segment(new { alg = "RS256", kid = SigningKeys.Kid }),
            Segment(new
            {
                iss = "https://accounts.google.com",
                aud = Audience,
                email = Caller,
                email_verified = true,
                exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/admin/users")
        {
            Content = JsonContent.Create(new { email, firstName = "T", lastName = "T", groupPath = "/my-app/editors" }),
        };
        request.Headers.Add("Authorization", $"Bearer {token}");
        return await _http!.SendAsync(request);
    }

    [Fact]
    public async Task An_external_domain_is_a_422_with_a_neutral_body_and_the_directory_is_not_called()
    {
        using var response = await PostAsync("r4@maroon.vip");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("EMAIL_DOMAIN_NOT_ALLOWED", body, StringComparison.Ordinal);
        Assert.DoesNotContain("maroon", body, StringComparison.Ordinal);
        Assert.DoesNotContain("example", body, StringComparison.Ordinal);
        Assert.Equal(0, _directory.CreateCalls);
    }

    [Fact]
    public async Task An_allowed_domain_is_created_through_the_same_route()
    {
        using var response = await PostAsync("someone@example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _directory.CreateCalls);
    }
}
