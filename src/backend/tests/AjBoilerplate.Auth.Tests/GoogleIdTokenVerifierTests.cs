using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AjBoilerplate.Auth.Events;
using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// Every rejection must say WHY, with the surface it happened on, at Warning.
/// </summary>
/// <remarks>
/// A rejection reason that stands for several checks ("claims mismatch" for issuer, audience, caller, e-mail verification and expiry)
/// is true, logged and useless. These tests exist so the specific reason cannot be collapsed back into a general one without a test
/// going red.
/// </remarks>
public sealed class GoogleIdTokenVerifierTests : IDisposable
{
    private const string Audience = "https://auth.example.com";
    private const string Caller = "admin-console@my-project.iam.gserviceaccount.com";

    private readonly CapturingLogger<GoogleIdTokenVerifier> _logger = new();
    private readonly SigningKeys _keys = new();
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private GoogleIdTokenVerifier Verifier()
    {
        var options = new AuthOptions();
        options.Admin.Audience = Audience;
        options.Admin.AllowedCallers.Add(Caller);

        // Every case signs with a key published through a fake Google JWKS, so the signature path runs for real. An unsigned
        // token is refused outright.
        return new GoogleIdTokenVerifier(_keys.Factory(), TestConfig.Wrap(options), _time, _logger);
    }

    private static string Segment(object value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    /// <summary>A properly signed token, as the calling service sends it in <c>Authorization</c>.</summary>
    private string Signed(object payload) =>
        $"Bearer {_keys.Sign(Segment(new { alg = "RS256", kid = SigningKeys.Kid }), Segment(payload))}";

    private object ValidPayload(object? overrides = null) => new
    {
        iss = "https://accounts.google.com",
        aud = Audience,
        email = Caller,
        email_verified = true,
        exp = _time.Now.AddMinutes(30).ToUnixTimeSeconds(),
    };

    private async Task<CapturedEntry> RejectAsync(string? header)
    {
        var allowed = await Verifier().IsAdminCallerAuthorizedAsync(header, CancellationToken.None);

        Assert.False(allowed);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("/internal/admin/*", entry.Field("Surface"));
        return entry;
    }

    [Fact]
    public async Task Accepts_a_signed_token_from_an_allowed_caller()
    {
        Assert.True(await Verifier().IsAdminCallerAuthorizedAsync(Signed(ValidPayload()), CancellationToken.None));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Missing_bearer_token() =>
        Assert.Equal("missing bearer token", (await RejectAsync(null)).Field("Reason"));

    [Fact]
    public async Task Missing_bearer_prefix_is_the_same_reason() =>
        Assert.Equal("missing bearer token", (await RejectAsync("Basic abc")).Field("Reason"));

    [Fact]
    public async Task Malformed_token() =>
        Assert.Equal("malformed token", (await RejectAsync("Bearer not-a-jwt")).Field("Reason"));

    [Fact]
    public async Task Undecodable_token() =>
        Assert.Equal("undecodable token", (await RejectAsync("Bearer !!!.???")).Field("Reason"));

    [Fact]
    public async Task Untrusted_issuer() =>
        Assert.Equal(
            "untrusted issuer",
            (await RejectAsync(Signed(new
            {
                iss = "https://login.example.invalid",
                aud = Audience,
                email = Caller,
                email_verified = true,
                exp = _time.Now.AddMinutes(30).ToUnixTimeSeconds(),
            }))).Field("Reason"));

    [Fact]
    public async Task Caller_not_in_the_allow_list_is_named_without_naming_the_caller()
    {
        var entry = await RejectAsync(Signed(new
        {
            iss = "https://accounts.google.com",
            aud = Audience,
            email = "someone-else@example.iam.gserviceaccount.com",
            email_verified = true,
            exp = _time.Now.AddMinutes(30).ToUnixTimeSeconds(),
        }));

        Assert.Equal("caller not in the allow-list", entry.Field("Reason"));

        // The reason is logged; the identity behind it is not. The allow-list is already in the
        // manifest, and an email is an identity rather than a diagnosis.
        Assert.DoesNotContain("someone-else", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_email_not_verified() =>
        Assert.Equal(
            "caller email not verified",
            (await RejectAsync(Signed(new
            {
                iss = "https://accounts.google.com",
                aud = Audience,
                email = Caller,
                email_verified = false,
                exp = _time.Now.AddMinutes(30).ToUnixTimeSeconds(),
            }))).Field("Reason"));

    [Fact]
    public async Task Token_expired() =>
        Assert.Equal(
            "token expired",
            (await RejectAsync(Signed(new
            {
                iss = "https://accounts.google.com",
                aud = Audience,
                email = Caller,
                email_verified = true,
                exp = _time.Now.AddMinutes(-30).ToUnixTimeSeconds(),
            }))).Field("Reason"));

    [Fact]
    public async Task No_readable_exp_claim() =>
        Assert.Equal(
            "no readable exp claim",
            (await RejectAsync(Signed(new
            {
                iss = "https://accounts.google.com",
                aud = Audience,
                email = Caller,
                email_verified = true,
            }))).Field("Reason"));

    [Fact]
    public async Task Unsigned_token_is_refused_with_its_own_reason() =>
        Assert.Equal(
            "unsigned token",
            (await RejectAsync($"Bearer {Segment(new { alg = "RS256", kid = SigningKeys.Kid })}.{Segment(ValidPayload())}")).Field("Reason"));

    [Fact]
    public async Task A_signature_from_a_key_google_did_not_publish_is_a_bad_signature()
    {
        using var stranger = new SigningKeys();
        var forged = stranger.Sign(Segment(new { alg = "RS256", kid = SigningKeys.Kid }), Segment(ValidPayload()));
        Assert.Equal("bad signature", (await RejectAsync($"Bearer {forged}")).Field("Reason"));
    }

    [Fact]
    public async Task Unreachable_signing_keys_fail_closed_with_a_distinct_reason()
    {
        var options = new AuthOptions();
        options.Admin.Audience = Audience;
        options.Admin.AllowedCallers.Add(Caller);
        var verifier = new GoogleIdTokenVerifier(new FailingHttpClientFactory(), TestConfig.Wrap(options), _time, _logger);

        Assert.False(await verifier.IsAdminCallerAuthorizedAsync(Signed(ValidPayload()), CancellationToken.None));
        Assert.Equal("signing keys unavailable", Assert.Single(_logger.Entries).Field("Reason"));
    }

    [Fact]
    public async Task Signing_keys_are_cached_and_an_unknown_kid_cannot_force_a_fetch_per_request()
    {
        var verifier = Verifier();
        Assert.True(await verifier.IsAdminCallerAuthorizedAsync(Signed(ValidPayload()), CancellationToken.None));
        Assert.True(await verifier.IsAdminCallerAuthorizedAsync(Signed(ValidPayload()), CancellationToken.None));
        Assert.Equal(1, _keys.Fetches);

        var unknownKid = _keys.Sign(Segment(new { alg = "RS256", kid = "invented" }), Segment(ValidPayload()));
        for (var i = 0; i < 5; i++)
        {
            Assert.False(await verifier.IsAdminCallerAuthorizedAsync($"Bearer {unknownKid}", CancellationToken.None));
        }

        Assert.Equal(1, _keys.Fetches);
    }

    [Fact]
    public async Task An_audience_mismatch_names_both_audiences_so_the_log_IS_the_diagnosis()
    {
        // The caller minted its token for another hostname of the same service. Both route here; the comparison is exact.
        const string presented = "https://auth-1234567890.region.run.app";

        var allowed = await Verifier().IsAdminCallerAuthorizedAsync(
            Signed(new
            {
                iss = "https://accounts.google.com",
                aud = presented,
                email = Caller,
                email_verified = true,
                exp = _time.Now.AddMinutes(30).ToUnixTimeSeconds(),
            }),
            CancellationToken.None);

        Assert.False(allowed);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("/internal/admin/*", entry.Field("Surface"));
        Assert.Equal(Audience, entry.Field("ExpectedAudience"));
        Assert.Equal(presented, entry.Field("PresentedAudience"));
    }

    [Fact]
    public async Task An_absent_audience_claim_says_so_rather_than_logging_an_empty_string()
    {
        var entry = await RejectAsync(Signed(new
        {
            iss = "https://accounts.google.com",
            email = Caller,
            email_verified = true,
            exp = _time.Now.AddMinutes(30).ToUnixTimeSeconds(),
        }));

        Assert.Equal("(absent or not a string)", entry.Field("PresentedAudience"));
    }

    [Fact]
    public async Task The_events_surface_is_labelled_as_itself_not_as_the_admin_one()
    {
        var options = new AuthOptions();
        options.Events.Audience = Audience;
        options.Events.PushServiceAccount = Caller;
        var verifier = new GoogleIdTokenVerifier(_keys.Factory(), TestConfig.Wrap(options), _time, _logger);

        Assert.False(await verifier.IsPushAuthorizedAsync(null, CancellationToken.None));
        Assert.Equal("/auth/events", Assert.Single(_logger.Entries).Field("Surface"));
    }

    public void Dispose() => _keys.Dispose();

    private sealed class FailingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Failing());

        private sealed class Failing : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("simulated: www.googleapis.com unreachable");
        }
    }
}
