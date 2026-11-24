using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AjBoilerplate.Auth.Tests;

/// <summary>
/// A test RSA key published as a Google-shaped JWKS, so the verifier's signature path runs for
/// real: it fetches the key set over the named HttpClient and verifies RS256 against it.
/// </summary>
internal sealed class SigningKeys : IDisposable
{
    public const string Kid = "test-kid-1";

    private readonly RSA _rsa = RSA.Create(2048);

    public int Fetches { get; private set; }

    public string Jwks()
    {
        var p = _rsa.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kid = Kid, kty = "RSA", alg = "RS256", use = "sig", n = Base64Url.EncodeToString(p.Modulus), e = Base64Url.EncodeToString(p.Exponent) },
            },
        });
    }

    /// <summary><c>header.payload.signature</c>, signed with the published key.</summary>
    public string Sign(string encodedHeader, string encodedPayload)
    {
        var signature = _rsa.SignData(Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{encodedHeader}.{encodedPayload}.{Base64Url.EncodeToString(signature)}";
    }

    public IHttpClientFactory Factory() => new JwksFactory(this);

    public void Dispose() => _rsa.Dispose();

    private sealed class JwksFactory(SigningKeys keys) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(keys));
    }

    private sealed class Handler(SigningKeys keys) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://www.googleapis.com/oauth2/v3/certs", request.RequestUri!.ToString());
            keys.Fetches++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(keys.Jwks()) });
        }
    }
}
