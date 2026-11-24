using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;

namespace AjBoilerplate.Auth.Identity;

internal sealed partial class EntraClient : IEntraClient
{
    private readonly HttpClient _http;
    private readonly EntraOptions _options;
    private readonly ILogger<EntraClient> _logger;

    public EntraClient(HttpClient http, IOptions<AuthOptions> options, ILogger<EntraClient> logger)
    {
        _http = http;
        _options = options.Value.Entra;
        _logger = logger;
    }

    public Uri BuildAuthorizeUrl(Uri redirectUri, string state, string codeChallenge, SignInPrompt prompt, string? loginHint = null)
    {
        var parameters = new List<KeyValuePair<string, string?>>(9)
        {
            new("client_id", _options.ClientId),
            new("response_type", "code"),
            new("response_mode", "query"),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("scope", _options.EffectiveScopes),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
        };

        if (prompt.ToParameterValue() is { } promptValue)
        {
            parameters.Add(new("prompt", promptValue));
        }

        if (!string.IsNullOrEmpty(loginHint))
        {
            parameters.Add(new("login_hint", loginHint));
        }

        return new Uri(_options.AuthorizeEndpoint + QueryString.Create(parameters).ToUriComponent());
    }

    public Task<EntraTokenResult> RedeemCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken) =>
        PostAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["code_verifier"] = codeVerifier,
                ["scope"] = _options.EffectiveScopes,
            },
            "authorization_code",
            cancellationToken);

    public Task<EntraTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = _options.EffectiveScopes,
            },
            "refresh_token",
            cancellationToken);

    private async Task<EntraTokenResult> PostAsync(Dictionary<string, string> form, string grant, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(_options.TokenEndpoint, content, cancellationToken);
            var body = await TokenEndpointResponse.ReadAsync(response, cancellationToken);

            if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(body?.AccessToken))
            {
                return new EntraTokenResult(EntraOutcome.Success, body.AccessToken, body.RefreshToken);
            }

            LogFailure(_logger, grant, (int)response.StatusCode, body?.Error ?? "(none)");

            return !TokenEndpointResponse.IsTransientStatus(response.StatusCode)
                && body?.Error is "invalid_grant" or "interaction_required" or "consent_required" or "login_required"
                ? EntraTokenResult.InvalidGrant
                : EntraTokenResult.Transient;
        }
        catch (HttpRequestException ex)
        {
            LogTransport(_logger, grant, ex.GetType().Name);
            return EntraTokenResult.Transient;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTransport(_logger, grant, "timeout");
            return EntraTokenResult.Transient;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entra {Grant} grant failed: HTTP {Status}, error {Error}.")]
    private static partial void LogFailure(ILogger logger, string grant, int status, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entra {Grant} grant failed in transport: {Reason}.")]
    private static partial void LogTransport(ILogger logger, string grant, string reason);
}
