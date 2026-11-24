using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Identity;

internal sealed partial class KeycloakExchanger : IKeycloakExchanger
{
    private const string TokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    private readonly HttpClient _http;
    private readonly KeycloakOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<KeycloakExchanger> _logger;

    public KeycloakExchanger(HttpClient http, IOptions<AuthOptions> options, TimeProvider time, ILogger<KeycloakExchanger> logger)
    {
        _http = http;
        _options = options.Value.Keycloak;
        _time = time;
        _logger = logger;
    }

    public async Task<ExchangeResult> ExchangeAsync(string entraAccessToken, PortalOptions portal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portal);

        var delay = _options.RetryBaseDelay;
        var attempt = 1;

        while (true)
        {
            var result = await AttemptAsync(entraAccessToken, portal, cancellationToken);
            if (result.Outcome != ExchangeOutcome.Transient || attempt >= _options.MaxAttempts)
            {
                return result;
            }

            attempt++;
            await Task.Delay(delay, _time, cancellationToken);
            delay *= 2;
        }
    }

    private async Task<ExchangeResult> AttemptAsync(string entraAccessToken, PortalOptions portal, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = TokenExchangeGrant,
            ["client_id"] = portal.ClientId,
            ["client_secret"] = portal.ClientSecret,
            ["subject_token"] = entraAccessToken,
            ["subject_token_type"] = AccessTokenType,
            ["scope"] = _options.Scope,
        };

        if (!string.IsNullOrWhiteSpace(_options.SubjectIssuer))
        {
            form["subject_issuer"] = _options.SubjectIssuer;
        }

        if (_options.SendAudience)
        {
            form["audience"] = portal.ClientId;
        }

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(_options.TokenEndpoint, content, cancellationToken);
            var body = await TokenEndpointResponse.ReadAsync(response, cancellationToken);

            if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(body?.AccessToken))
            {
                LogSucceeded(_logger, portal.ClientId);
                return new ExchangeResult(ExchangeOutcome.Success, body.AccessToken);
            }

            var error = body?.Error ?? "(none)";
            LogFailure(_logger, portal.ClientId, (int)response.StatusCode, error);

            if (TokenEndpointResponse.IsTransientStatus(response.StatusCode))
            {
                return ExchangeResult.Transient;
            }

            if (error is "invalid_client" or "unauthorized_client" or "invalid_request" or "unsupported_grant_type")
            {
                LogMisconfigured(_logger, portal.ClientId, error);
                return ExchangeResult.Transient;
            }

            return ExchangeResult.Denied;
        }
        catch (HttpRequestException ex)
        {
            LogTransport(_logger, portal.ClientId, ex.GetType().Name);
            return ExchangeResult.Transient;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTransport(_logger, portal.ClientId, "timeout");
            return ExchangeResult.Transient;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak exchange for {ClientId} succeeded.")]
    private static partial void LogSucceeded(ILogger logger, string clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak exchange for {ClientId} failed: HTTP {Status}, error {Error}.")]
    private static partial void LogFailure(ILogger logger, string clientId, int status, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak rejected OUR client {ClientId} ({Error}): check its secret and token-exchange permission.")]
    private static partial void LogMisconfigured(ILogger logger, string clientId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak exchange for {ClientId} failed in transport: {Reason}.")]
    private static partial void LogTransport(ILogger logger, string clientId, string reason);
}
