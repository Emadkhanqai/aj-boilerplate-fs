using System.Net;
using System.Net.Http.Headers;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.IntegrationTests.Support;

namespace AjBoilerplate.IntegrationTests.Api;

/// <summary>
/// The cross-cutting pipeline itself — security headers, envelope coverage of framework-generated
/// replies, and the health endpoints. None of this belongs to any one feature, which is exactly why
/// it is the first thing to break silently when the middleware order is changed.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PipelineTests
{
    private readonly ApiFactory _factory;

    public PipelineTests(SqlServerFixture fixture) => _factory = fixture.Api;

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "no-referrer")]
    [InlineData("Cross-Origin-Opener-Policy", "same-origin")]
    [InlineData("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'")]
    public async Task Security_headers_are_present_on_a_successful_response(string header, string expected)
    {
        var response = await _factory.CreateClientAs(ApplicationRoles.Viewer).GetAsync("/api/v1/items", CancellationToken.None);

        Assert.True(response.Headers.TryGetValues(header, out var values), $"{header} was missing.");
        Assert.Equal(expected, string.Join(", ", values!));
    }

    [Fact]
    public async Task Security_headers_are_present_on_an_error_response()
    {
        // The security-headers middleware runs first precisely so that error replies — produced
        // much further down the pipeline — are covered too. This is where they usually go missing.
        var response = await _factory.CreateClient().GetAsync("/api/v1/items", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }

    [Fact]
    public async Task An_unmatched_route_returns_an_enveloped_404()
    {
        var response = await _factory.CreateClientAs(ApplicationRoles.Viewer)
            .GetAsync("/api/v1/does-not-exist", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var envelope = await response.Content.ReadErrorEnvelopeAsync();
        Assert.False(envelope.IsSuccess);
        Assert.Equal(EnvelopeCodes.NotFound, envelope.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.TraceId));
    }

    [Fact]
    public async Task A_wrong_verb_returns_an_enveloped_405()
    {
        var client = _factory.CreateClientAs(ApplicationRoles.Editor);
        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/items")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(EnvelopeCodes.MethodNotAllowed, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task An_unsupported_content_type_returns_an_enveloped_415()
    {
        var client = _factory.CreateClientAs(ApplicationRoles.Editor);
        var content = new StringContent("name=Widget");
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var response = await client.PostAsync("/api/v1/items", content, CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(EnvelopeCodes.UnsupportedMediaType, (await response.Content.ReadErrorEnvelopeAsync()).Code);
    }

    [Fact]
    public async Task A_malformed_json_body_returns_an_enveloped_validation_400()
    {
        // [ApiController]'s model-binding failure short-circuits before any action or
        // FluentValidation runs, so this path is enveloped by the result filter rather than by the
        // exception handler — and it must look identical to the caller.
        var client = _factory.CreateClientAs(ApplicationRoles.Editor);
        var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/items", content, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadErrorEnvelopeAsync();
        Assert.Equal(EnvelopeCodes.ValidationError, envelope.Code);
        // The framework's type name must never leak into a response body.
        Assert.DoesNotContain("ProblemDetails", envelope.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_liveness_probe_is_anonymous_and_healthy()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_bare_health_path_is_aliased_to_liveness() =>
        // Aliased to LIVENESS, not readiness, so a transient database blip cannot flap a healthy
        // instance out of the load balancer's rotation.
        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.CreateClient().GetAsync("/health", CancellationToken.None)).StatusCode);
}
