using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AjBoilerplate.Application.Administration;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Admin;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Infrastructure.Identity;
using AjBoilerplate.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// The admin user screens' API against a STUB AUTH SERVICE: the real controller, policy, envelope, typed client and
/// status mapping, with the network replaced by an in-process handler that answers like the Auth service's
/// <c>/internal/admin/*</c>. What it does not prove is the real Auth service's behaviour — that is its own suite.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UserAdminApiTests : IDisposable
{
    private const string AdminKey = "stub-admin-api-key";

    private readonly StubAuthService _auth = new();
    private readonly WebApplicationFactory<Program> _factory;

    public UserAdminApiTests(SqlServerFixture fixture) =>
        _factory = fixture.Api.WithWebHostBuilder(b =>
        {
            b.UseSetting("AuthService:BaseUrl", "https://auth.stub.internal");
            b.UseSetting("AuthService:AdminApiKey", AdminKey);
            b.ConfigureTestServices(services => services
                .AddHttpClient<IIdentityAdministration, AuthServiceIdentityAdministration>()
                .ConfigurePrimaryHttpMessageHandler(() => _auth));
        });

    public void Dispose() => _factory.Dispose();

    private HttpClient Admin() => _factory.CreateClientAsRole(ApplicationRoles.Admin);

    [Fact]
    public async Task Search_returns_the_users_enveloped_and_the_upstream_got_the_key_and_the_term()
    {
        _auth.Respond(HttpStatusCode.OK, """{"users":[{"subject":"s1","username":"jane","email":"jane@x.org","firstName":"Jane","lastName":"Doe","enabled":true}]}""");

        var response = await Admin().GetAsync("/api/v1/admin/users?q=jane&limit=5", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadEnvelopeAsync<DirectoryUsersResponse>();
        Assert.Equal("s1", Assert.Single(data.Users).Subject);
        var call = Assert.Single(_auth.Calls);
        Assert.Equal(("GET", "/internal/admin/users", "?q=jane&limit=5", AdminKey), (call.Method, call.Path, call.Query, call.Key));
    }

    [Fact]
    public async Task The_wire_names_are_the_ones_the_users_screen_binds_to()
    {
        _auth.Respond(HttpStatusCode.OK, """{"users":[{"subject":"s1","username":"jane","email":"j@x.org","firstName":"J","lastName":"D","enabled":false}]}""");

        var response = await Admin().GetAsync("/api/v1/admin/users?q=jane", CancellationToken.None);

        using var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var user = raw.RootElement.GetProperty("data").GetProperty("users")[0];
        Assert.Equal(
            new[] { "email", "enabled", "firstName", "lastName", "subject", "username" }.Order(StringComparer.Ordinal),
            user.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Groups_and_members_pass_through_with_the_portal_coverage()
    {
        _auth.Respond(HttpStatusCode.OK, """{"groups":[{"path":"/App/editor","name":"editor","parentPath":"/App"}],"portals":[{"path":"/App","outcome":"Success"}]}""");
        var groups = await (await Admin().GetAsync("/api/v1/admin/groups", CancellationToken.None)).Content.ReadEnvelopeAsync<DirectoryGroupsResponse>();

        _auth.Respond(HttpStatusCode.OK, """{"members":[{"user":{"subject":"s1","username":"u","enabled":true},"groups":["/App/editor"]}],"portals":[{"path":"/App","outcome":"Unavailable"}]}""");
        var members = await (await Admin().GetAsync("/api/v1/admin/members", CancellationToken.None)).Content.ReadEnvelopeAsync<DirectoryMembersResponse>();

        Assert.Equal("/App/editor", Assert.Single(groups.Groups).Path);
        Assert.Equal("Success", Assert.Single(groups.Portals).Outcome);
        Assert.Equal(["/App/editor"], Assert.Single(members.Members).Groups);

        // A portal the upstream could not read is reported, so a screen cannot read "no rows" as "no seat holders".
        Assert.Equal("Unavailable", Assert.Single(members.Portals).Outcome);
    }

    [Fact]
    public async Task Setting_groups_sends_the_full_set_and_reports_what_changed()
    {
        _auth.Respond(HttpStatusCode.OK, """{"groups":["/App/editor"],"added":["/App/editor"],"removed":["/App/viewer"],"portals":[]}""");

        var response = await Admin().PutAsJsonAsync("/api/v1/admin/users/s1/groups", new SetUserGroupsRequest { Groups = ["/App/editor"] }, CancellationToken.None);

        var data = await response.Content.ReadEnvelopeAsync<UserGroupsResponse>();
        Assert.Equal(["/App/editor"], data.Added);
        Assert.Equal(["/App/viewer"], data.Removed);
        var call = Assert.Single(_auth.Calls);
        Assert.Equal(("PUT", "/internal/admin/users/s1/groups"), (call.Method, call.Path));
        Assert.Equal("""{"groups":["/App/editor"]}""", call.Body);
    }

    [Fact]
    public async Task Setting_groups_without_a_groups_array_is_a_400_and_never_reaches_upstream()
    {
        var response = await Admin().PutAsJsonAsync("/api/v1/admin/users/s1/groups", new { }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(EnvelopeCodes.ValidationError, (await response.Content.ReadErrorEnvelopeAsync()).Code);
        Assert.Empty(_auth.Calls);
    }

    [Fact]
    public async Task Create_user_returns_the_user_and_a_missing_email_or_group_is_a_400()
    {
        _auth.Respond(HttpStatusCode.OK, """{"user":{"subject":"s9","username":"n","email":"n@x.org","enabled":true},"groups":["/App/viewer"]}""");

        var created = await Admin().PostAsJsonAsync("/api/v1/admin/users", new CreateDirectoryUserRequest { Email = "n@x.org", GroupPath = "/App/viewer" }, CancellationToken.None);
        var missing = await Admin().PostAsJsonAsync("/api/v1/admin/users", new CreateDirectoryUserRequest { Email = "n@x.org" }, CancellationToken.None);

        Assert.Equal("s9", (await created.Content.ReadEnvelopeAsync<CreatedDirectoryUserResponse>()).User.Subject);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Single(_auth.Calls);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("enable")]
    [InlineData("refresh")]
    public async Task Disable_enable_and_refresh_are_204_and_each_is_one_post_to_the_matching_upstream_route(string action)
    {
        _auth.Respond(action == "refresh" ? HttpStatusCode.OK : HttpStatusCode.NoContent, action == "refresh" ? """{"flagged":2}""" : string.Empty);

        var response = await Admin().PostAsync($"/api/v1/admin/users/s1/{action}", content: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var call = Assert.Single(_auth.Calls);
        Assert.Equal(("POST", $"/internal/admin/users/s1/{action}"), (call.Method, call.Path));
    }

    public static TheoryData<int, string?, HttpStatusCode, string> UpstreamCases => new()
    {
        { 404, """{"error":"that subject holds no seat in any portal this installation administers"}""", HttpStatusCode.NotFound, EnvelopeCodes.NotFound },
        { 404, null, HttpStatusCode.ServiceUnavailable, EnvelopeCodes.UpstreamError },
        { 400, """{"error":"one or more group paths are not seats"}""", HttpStatusCode.BadRequest, EnvelopeCodes.ValidationError },
        { 401, null, HttpStatusCode.BadGateway, EnvelopeCodes.UpstreamError },
        { 403, null, HttpStatusCode.BadGateway, EnvelopeCodes.UpstreamError },
        { 409, """{"error":"AMBIGUOUS_USER"}""", HttpStatusCode.BadGateway, EnvelopeCodes.UpstreamError },
        { 501, null, HttpStatusCode.NotImplemented, EnvelopeCodes.UpstreamError },
        { 503, null, HttpStatusCode.ServiceUnavailable, EnvelopeCodes.UpstreamError },
        { 500, "<html>boom</html>", HttpStatusCode.ServiceUnavailable, EnvelopeCodes.UpstreamError },
    };

    [Theory]
    [MemberData(nameof(UpstreamCases))]
    public async Task An_upstream_failure_is_never_a_403_to_the_administrator_and_each_has_its_own_envelope(
        int upstreamStatus, string? upstreamBody, HttpStatusCode expected, string expectedCode)
    {
        _auth.Respond((HttpStatusCode)upstreamStatus, upstreamBody ?? string.Empty);

        var response = await Admin().GetAsync("/api/v1/admin/users/s1/groups", CancellationToken.None);

        Assert.Equal(expected, response.StatusCode);
        var envelope = await response.Content.ReadErrorEnvelopeAsync();
        Assert.Equal((false, expectedCode), (envelope.IsSuccess, envelope.Code));
        Assert.False(string.IsNullOrWhiteSpace(envelope.TraceId));
    }

    [Fact]
    public async Task A_refused_email_domain_is_a_422_with_its_own_code_on_create()
    {
        _auth.Respond(HttpStatusCode.UnprocessableEntity, """{"error":"EMAIL_DOMAIN_NOT_ALLOWED","message":"x"}""");

        var response = await Admin().PostAsJsonAsync("/api/v1/admin/users", new CreateDirectoryUserRequest { Email = "a@blocked.example", GroupPath = "/App/viewer" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(EnvelopeCodes.EmailDomainNotAllowed, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task An_unreachable_Auth_service_is_a_503()
    {
        _auth.Throw(new HttpRequestException("connection refused"));

        var response = await Admin().PostAsync("/api/v1/admin/users/s1/disable", content: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Without_AuthService_configuration_every_route_is_a_503_and_nothing_is_sent()
    {
        var calls = new StubAuthService();
        using var unconfigured = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("AuthService:BaseUrl", string.Empty);
            b.UseSetting("AuthService:AdminApiKey", string.Empty);
            b.ConfigureTestServices(s => s.AddHttpClient<IIdentityAdministration, AuthServiceIdentityAdministration>()
                .ConfigurePrimaryHttpMessageHandler(() => calls));
        });

        var response = await unconfigured.CreateClientAsRole(ApplicationRoles.Admin).GetAsync("/api/v1/admin/groups", CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(calls.Calls);
    }

    [Theory]
    [InlineData("GET", "/api/v1/admin/users?q=a")]
    [InlineData("GET", "/api/v1/admin/members")]
    [InlineData("GET", "/api/v1/admin/groups")]
    [InlineData("GET", "/api/v1/admin/users/s1/groups")]
    [InlineData("PUT", "/api/v1/admin/users/s1/groups")]
    [InlineData("POST", "/api/v1/admin/users")]
    [InlineData("POST", "/api/v1/admin/users/s1/disable")]
    [InlineData("POST", "/api/v1/admin/users/s1/enable")]
    [InlineData("POST", "/api/v1/admin/users/s1/refresh")]
    public async Task Only_an_administrator_may_call_any_of_them_and_upstream_is_never_asked_for_anyone_else(string method, string url)
    {
        async Task<HttpStatusCode> Send(HttpClient client)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method != "GET")
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            return (await client.SendAsync(request, CancellationToken.None)).StatusCode;
        }

        var anonymous = await Send(_factory.CreateClient());
        var viewer = await Send(_factory.CreateClientAsRole(ApplicationRoles.Viewer));
        var editor = await Send(_factory.CreateClientAsRole(ApplicationRoles.Editor));

        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (anonymous, viewer, editor));
        Assert.Empty(_auth.Calls);
    }

    private sealed class StubAuthService : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _next = () => new HttpResponseMessage(HttpStatusCode.OK);

        public List<(string Method, string Path, string Query, string? Key, string Body)> Calls { get; } = [];

        public void Respond(HttpStatusCode status, string body) =>
            _next = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public void Throw(Exception exception) => _next = () => throw exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Headers.TryGetValues("X-Admin-Api-Key", out var key) ? key.Single() : null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _next();
        }
    }
}
