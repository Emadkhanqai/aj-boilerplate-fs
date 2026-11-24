using System.Net;
using System.Text;
using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Administration;
using AjBoilerplate.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.UnitTests.Administration;

public sealed class AuthServiceIdentityAdministrationTests
{
    private const string Key = "super-secret-admin-key-value";
    private const string Subject = "subject-with-personal-id-42";
    private const string SearchTerm = "jane.doe@example.org";

    private readonly StubHandler _handler = new();
    private readonly CapturingLogger _logger = new();

    private AuthServiceIdentityAdministration Client(string baseUrl = "https://auth.internal", string key = Key) =>
        new(
            new HttpClient(_handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") },
            Options.Create(new AuthServiceOptions { BaseUrl = baseUrl, AdminApiKey = key }),
            new FixedCorrelation(),
            _logger);

    [Fact]
    public async Task A_search_sends_the_key_and_the_term_and_reads_the_users()
    {
        _handler.Respond(HttpStatusCode.OK, """{"users":[{"subject":"s1","username":"jane","email":"jane@x.org","firstName":"Jane","lastName":"Doe","enabled":true}]}""");

        var (outcome, users) = await Client().SearchUsersAsync(SearchTerm, 500, CancellationToken.None);

        Assert.Equal(IdentityAdminOutcome.Success, outcome);
        Assert.Equal("s1", Assert.Single(users).Subject);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Key, request.Headers["X-Admin-Api-Key"]);
        Assert.Contains("q=jane.doe%40example.org", request.Uri);
        Assert.Contains("limit=100", request.Uri);
    }

    [Fact]
    public async Task A_blank_term_never_reaches_the_network()
    {
        var (outcome, users) = await Client().SearchUsersAsync("  ", 25, CancellationToken.None);

        Assert.Equal((IdentityAdminOutcome.Success, 0), (outcome, users.Count));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData(400, IdentityAdminOutcome.GroupNotFound)]
    [InlineData(401, IdentityAdminOutcome.Denied)]
    [InlineData(403, IdentityAdminOutcome.PortalNotPermitted)]
    [InlineData(409, IdentityAdminOutcome.UpstreamRejected)]
    [InlineData(501, IdentityAdminOutcome.NotSupportedByUpstream)]
    [InlineData(502, IdentityAdminOutcome.Denied)]
    [InlineData(503, IdentityAdminOutcome.Unavailable)]
    [InlineData(500, IdentityAdminOutcome.Unavailable)]
    public async Task Each_upstream_status_maps_to_its_own_outcome_and_is_logged_raw_before_it_is_folded(int status, IdentityAdminOutcome expected)
    {
        _handler.Respond((HttpStatusCode)status, "{}");

        var result = await Client().DisableAsync(Subject, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Contains(_logger.Messages, m => m.Contains($"upstreamStatus={status}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_the_no_seat_404_means_the_user_was_not_found_any_other_404_is_the_surface_being_off()
    {
        _handler.Respond(HttpStatusCode.NotFound, """{"error":"that subject holds no seat in any portal this installation administers"}""");
        Assert.Equal(IdentityAdminOutcome.UserNotFound, (await Client().GetGroupsAsync(Subject, CancellationToken.None)).Outcome);

        _handler.Respond(HttpStatusCode.NotFound, string.Empty);
        Assert.Equal(IdentityAdminOutcome.Unavailable, (await Client().GetGroupsAsync(Subject, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_refused_domain_is_its_own_outcome_and_a_422_with_another_body_is_not()
    {
        var user = new NewDirectoryUser("a@blocked.example", "A", "B", "/App/editor");

        _handler.Respond(HttpStatusCode.UnprocessableEntity, """{"error":"EMAIL_DOMAIN_NOT_ALLOWED"}""");
        Assert.Equal(IdentityAdminOutcome.EmailDomainNotAllowed, (await Client().CreateUserAsync(user, CancellationToken.None)).Outcome);

        _handler.Respond(HttpStatusCode.UnprocessableEntity, """{"error":"something else"}""");
        Assert.Equal(IdentityAdminOutcome.Unavailable, (await Client().CreateUserAsync(user, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_failed_write_still_reports_what_it_changed()
    {
        _handler.Respond(HttpStatusCode.ServiceUnavailable, """{"groups":["/App/editor"],"added":["/App/editor"],"removed":[],"portals":[{"path":"/App","outcome":"Unavailable"}]}""");

        var result = await Client().SetGroupsAsync(Subject, ["/App/editor"], CancellationToken.None);

        Assert.Equal(IdentityAdminOutcome.Unavailable, result.Outcome);
        Assert.Equal(["/App/editor"], result.Added);
        Assert.Equal(IdentityAdminOutcome.Unavailable, Assert.Single(result.Portals!).Outcome);
    }

    [Fact]
    public async Task A_successful_create_carries_the_user_and_the_email_travels_in_the_body_not_the_url()
    {
        _handler.Respond(HttpStatusCode.OK, """{"user":{"subject":"s9","username":"new","email":"new@x.org","firstName":"N","lastName":"U","enabled":true},"groups":["/App/viewer"]}""");

        var result = await Client().CreateUserAsync(new NewDirectoryUser("new@x.org", "N", "U", "/App/viewer"), CancellationToken.None);

        Assert.Equal("s9", result.User!.Subject);
        var request = Assert.Single(_handler.Requests);
        Assert.DoesNotContain("new@x.org", request.Uri, StringComparison.Ordinal);
        Assert.Contains("new@x.org", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_create_the_upstream_accepted_but_returned_no_user_for_is_not_a_success()
    {
        _handler.Respond(HttpStatusCode.OK, "{}");

        var result = await Client().CreateUserAsync(new NewDirectoryUser("a@x.org", "A", "B", "/App/viewer"), CancellationToken.None);

        Assert.Equal(IdentityAdminOutcome.Unavailable, result.Outcome);
    }

    [Theory]
    [InlineData("", Key)]
    [InlineData("https://auth.internal", "")]
    [InlineData("not a url", Key)]
    public async Task Unconfigured_is_unavailable_and_sends_nothing(string baseUrl, string key)
    {
        var client = new AuthServiceIdentityAdministration(
            new HttpClient(_handler), Options.Create(new AuthServiceOptions { BaseUrl = baseUrl, AdminApiKey = key }),
            new FixedCorrelation(), _logger);

        Assert.Equal(IdentityAdminOutcome.Unavailable, (await client.EnableAsync(Subject, CancellationToken.None)).Outcome);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task An_unreachable_upstream_is_unavailable()
    {
        _handler.Throw(new HttpRequestException("connection refused"));

        Assert.Equal(IdentityAdminOutcome.Unavailable, (await Client().DisableAsync(Subject, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task No_log_line_carries_the_key_the_subject_the_search_term_or_a_query_string()
    {
        _handler.Respond(HttpStatusCode.BadGateway, $$"""{"error":"{{SearchTerm}} {{Subject}} {{Key}}"}""");
        await Client().SearchUsersAsync(SearchTerm, 25, CancellationToken.None);
        await Client().SetGroupsAsync(Subject, ["/App/editor"], CancellationToken.None);
        _handler.Throw(new HttpRequestException($"could not reach https://auth.internal/internal/admin/users?q={SearchTerm}"));
        await Client().SearchUsersAsync(SearchTerm, 25, CancellationToken.None);

        // Positive control: the lines exist and carry the templates, so an empty log cannot pass this.
        Assert.Equal(3, _logger.Messages.Count);
        Assert.Contains(_logger.Messages, m => m.Contains("endpoint=internal/admin/users ", StringComparison.Ordinal));
        Assert.Contains(_logger.Messages, m => m.Contains("endpoint=internal/admin/users/{subject}/groups", StringComparison.Ordinal));
        Assert.Contains(_logger.Messages, m => m.Contains("correlationId=trace-1", StringComparison.Ordinal));

        foreach (var message in _logger.Messages)
        {
            Assert.DoesNotContain(Key, message, StringComparison.Ordinal);
            Assert.DoesNotContain(Subject, message, StringComparison.Ordinal);
            Assert.DoesNotContain("jane.doe", message, StringComparison.Ordinal);
            Assert.DoesNotContain("example.org", message, StringComparison.Ordinal);
            Assert.DoesNotContain("?q=", message, StringComparison.Ordinal);
        }
    }

    private sealed class FixedCorrelation : ICorrelationContext
    {
        public string? CorrelationId => "trace-1";
    }

    private sealed class CapturingLogger : ILogger<AuthServiceIdentityAdministration>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            // Message AND the exception text: an exception's own message is where a URL would leak from.
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _next = () => new HttpResponseMessage(HttpStatusCode.OK);

        public List<(string Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body) =>
            _next = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public void Throw(Exception exception) => _next = () => throw exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _next();
        }
    }
}
