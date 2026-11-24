using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using AjBoilerplate.Auth.Events;
using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// A token with no signature is never accepted, on either surface, whatever the configuration says.
/// </summary>
/// <remarks>
/// On Cloud Run the platform authenticates on <c>X-Serverless-Authorization</c> when it is present and forwards <c>Authorization</c>
/// untouched, so any invoker could put a real token in the first header and a hand-built unsigned one claiming to be an allowed caller
/// in the second. The service cannot tell "stripped by the platform" from "never had one", so it accepts neither. The options are bound
/// from CONFIGURATION here, as the service binds them.
/// </remarks>
public sealed class UnsignedTokenTests : IDisposable
{
    private const string Audience = "https://auth.example.com";
    private const string Caller = "admin-console@my-project.iam.gserviceaccount.com";

    private readonly CapturingLogger<GoogleIdTokenVerifier> _logger = new();
    private readonly SigningKeys _keys = new();
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private GoogleIdTokenVerifier Verifier()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Admin:Audience"] = Audience,
            ["Auth:Admin:AllowedCallers:0"] = Caller,
            ["Auth:Events:Audience"] = Audience,
            ["Auth:Events:PushServiceAccount"] = Caller,
        }).Build();

        var options = new AuthOptions();
        config.GetSection(AuthOptions.SectionName).Bind(options);
        return new GoogleIdTokenVerifier(_keys.Factory(), TestConfig.Wrap(options), _time, _logger);
    }

    private string Payload() => Segment(new
    {
        iss = "https://accounts.google.com",
        aud = Audience,
        email = Caller,
        email_verified = true,
        exp = _time.Now.AddMinutes(30).ToUnixTimeSeconds(),
    });

    private static string Segment(object value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static TheoryData<string> UnsignedShapes() =>
    [
        "two segments",            // header.payload
        "empty third segment",     // header.payload.
        "alg none",                // {"alg":"none"}.payload.
    ];

    private string Unsigned(string shape) => shape switch
    {
        "two segments" => $"Bearer {Segment(new { alg = "RS256", kid = SigningKeys.Kid })}.{Payload()}",
        "empty third segment" => $"Bearer {Segment(new { alg = "RS256", kid = SigningKeys.Kid })}.{Payload()}.",
        _ => $"Bearer {Segment(new { alg = "none" })}.{Payload()}.",
    };

    [Theory]
    [MemberData(nameof(UnsignedShapes))]
    public async Task Admin_surface_refuses_an_unsigned_token(string shape)
    {
        Assert.False(await Verifier().IsAdminCallerAuthorizedAsync(Unsigned(shape), CancellationToken.None));
        Assert.Equal("/internal/admin/*", Assert.Single(_logger.Entries).Field("Surface"));
    }

    [Theory]
    [MemberData(nameof(UnsignedShapes))]
    public async Task Events_surface_refuses_an_unsigned_token(string shape)
    {
        Assert.False(await Verifier().IsPushAuthorizedAsync(Unsigned(shape), CancellationToken.None));
        Assert.Equal("/auth/events", Assert.Single(_logger.Entries).Field("Surface"));
    }

    /// <summary>Positive control: the same claims, properly signed, ARE accepted, so the refusals
    /// above are about the signature and not about a claim the test got wrong.</summary>
    [Fact]
    public async Task The_same_claims_signed_by_a_published_key_are_accepted()
    {
        var config = new AuthOptions();
        config.Admin.Audience = Audience;
        config.Admin.AllowedCallers.Add(Caller);
        var verifier = new GoogleIdTokenVerifier(_keys.Factory(), TestConfig.Wrap(config), _time, _logger);

        var header = Segment(new { alg = "RS256", kid = SigningKeys.Kid });
        Assert.True(await verifier.IsAdminCallerAuthorizedAsync($"Bearer {_keys.Sign(header, Payload())}", CancellationToken.None));
        Assert.Empty(_logger.Entries);
    }

    public void Dispose() => _keys.Dispose();
}
