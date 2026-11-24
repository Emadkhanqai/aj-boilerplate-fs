using System.Net;
using System.Security.Cryptography;
using AjBoilerplate.Api.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AjBoilerplate.UnitTests.Identity;

/// <summary>The JWKS cache: filled at startup, failing soft, and not refetched on every request.</summary>
public sealed class KeycloakSigningKeyProviderTests
{
    [Fact]
    public async Task Warm_up_loads_the_keys_so_the_first_request_needs_no_fetch()
    {
        var handler = new CountingHandler(JwksJson("k1", "k2"));
        var provider = ProviderFor(handler);

        await provider.WarmUpAsync();
        var key = Assert.Single(provider.GetSigningKeys("k1"));

        Assert.Equal("k1", key.KeyId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Warm_up_against_an_unreachable_endpoint_does_not_throw_and_the_first_request_fetches()
    {
        var handler = new CountingHandler(JwksJson("k1")) { FailFirst = true };
        var provider = ProviderFor(handler);

        await provider.WarmUpAsync();
        Assert.Equal(1, handler.Calls);

        // The failed warm-up did not stamp the cooldown, so this request really does fetch.
        Assert.Single(provider.GetSigningKeys("k1"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task An_unknown_key_id_fails_closed_and_a_second_attempt_inside_the_cooldown_does_not_refetch()
    {
        var handler = new CountingHandler(JwksJson("k1"));
        var provider = ProviderFor(handler);
        await provider.WarmUpAsync();

        Assert.Empty(provider.GetSigningKeys("forged"));
        Assert.Empty(provider.GetSigningKeys("forged"));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void A_token_with_no_key_id_gets_no_keys() =>
        Assert.Empty(ProviderFor(new CountingHandler(JwksJson("k1"))).GetSigningKeys(null));

    private static KeycloakSigningKeyProvider ProviderFor(CountingHandler handler) =>
        new(
            new HandlerFactory(handler),
            Options.Create(new KeycloakOptions { JwksUri = "https://keycloak.example.com/certs" }),
            NullLogger<KeycloakSigningKeyProvider>.Instance);

    private static string JwksJson(params string[] kids)
    {
        var keys = kids.Select(kid =>
        {
            using var rsa = RSA.Create(2048);
            var p = rsa.ExportParameters(false);
            return $$"""{"kty":"RSA","kid":"{{kid}}","use":"sig","n":"{{Base64UrlEncoder.Encode(p.Modulus)}}","e":"{{Base64UrlEncoder.Encode(p.Exponent)}}"}""";
        });
        return $$"""{"keys":[{{string.Join(",", keys)}}]}""";
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool FailFirst { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailFirst && Calls == 1)
            {
                throw new HttpRequestException("no route to host");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
