using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.SessionEvents;

/// <summary>
/// POSTs one session-ended event to one portal backend, exactly as docs/auth/session-ended-contract.md specifies, and classifies
/// the answer. Never throws for a delivery failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways to authenticate, chosen per portal.</b> With <c>SessionEvents:Key</c> set the call carries
/// <c>X-Auth-Events-Key</c> (a shared secret the receiver compares in constant time); otherwise a Google ID token goes in BOTH
/// <c>Authorization</c> and <c>X-Serverless-Authorization</c>. Cloud Run's IAM layer reads the second to admit the request; the
/// receiver's own Google-ID-token scheme reads the first to know WHICH workload called, so sending only the platform header passes
/// IAM and then gets a 401 from the application. Both, from one mint.
/// </para>
/// <para>
/// With a key set no token is minted at all, so a deployment off Google Cloud never waits on a metadata server.
/// </para>
/// </remarks>
internal sealed class HttpSessionEndedSender : ISessionEndedSender
{
    public const string HttpClientName = "session-ended";

    /// <summary>The shared-key header. The receiver compares it in constant time.</summary>
    public const string KeyHeader = "X-Auth-Events-Key";

    /// <summary>The value a portal stores as the identity provider of a gateway sign-in.</summary>
    public const string IdentityProvider = "Auth Backend (Keycloak)";

    /// <summary>The receiver outcomes the contract defines. Anything else in a 2xx body is logged as
    /// <c>acknowledged</c>, so a portal's free text never reaches this service's log.</summary>
    private static readonly HashSet<string> KnownOutcomes = new(StringComparer.Ordinal)
    {
        "recorded", "duplicate", "ignored-unknown-session",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IHttpClientFactory _http;
    private readonly IGoogleIdTokenSource _tokens;

    public HttpSessionEndedSender(IHttpClientFactory http, IGoogleIdTokenSource tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<SessionEndedDelivery> SendAsync(SessionEndedEvent sessionEnded, SessionEventsOptions target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEnded);
        ArgumentNullException.ThrowIfNull(target);

        using var request = new HttpRequestMessage(HttpMethod.Post, target.Url)
        {
            Content = JsonContent.Create(Payload.From(sessionEnded), options: Json),
        };

        if (target.UsesKey)
        {
            request.Headers.TryAddWithoutValidation(KeyHeader, target.Key);
        }
        else
        {
            var audience = target.ResolvedAudience;
            var token = audience is null ? null : await _tokens.GetAsync(audience, cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                return new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "id_token_unavailable");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Serverless-Authorization", "Bearer " + token);
        }

        // 32 hex: passes the id-shaped sanitisers the portals apply to a request id, so their own
        // log line for this request carries the event id too.
        request.Headers.TryAddWithoutValidation("X-Request-Id", sessionEnded.EventId.ToString("N"));

        try
        {
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new SessionEndedDelivery(SessionEndedDeliveryKind.Delivered, status, await OutcomeAsync(response, cancellationToken));
            }

            return new SessionEndedDelivery(Classify(status), status, "http_" + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return new SessionEndedDelivery(SessionEndedDeliveryKind.Retry, null, "transport");
        }
    }

    /// <summary>
    /// Retry what time or configuration can fix; give up only on what resending the same bytes
    /// cannot. 404 is retried on purpose: it is what a portal answers before its receiver is
    /// deployed. 401/403 are retried on purpose: they are what a key or an allowed caller that is not configured yet on the
    /// receiver looks like, and both get fixed.
    /// </summary>
    internal static SessionEndedDeliveryKind Classify(int status) => status switch
    {
        401 or 403 or 404 or 408 or 429 => SessionEndedDeliveryKind.Retry,
        >= 500 => SessionEndedDeliveryKind.Retry,
        _ => SessionEndedDeliveryKind.Permanent,
    };

    private static async Task<string> OutcomeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("outcome", out var outcome)
                && outcome.ValueKind == JsonValueKind.String
                && KnownOutcomes.Contains(outcome.GetString()!))
            {
                return outcome.GetString()!;
            }
        }
        catch (JsonException)
        {
            // A 2xx is an acknowledgement whatever its body says; the body is only for the log.
        }

        return "acknowledged";
    }

    /// <summary>The wire body (docs/auth/session-ended-contract.md, "Payload").</summary>
    internal sealed record Payload(
        int SchemaVersion,
        string EventType,
        string EventId,
        string SessionId,
        string Reason,
        string ReasonDetail,
        string OccurredAtUtc,
        string? SessionStartedAtUtc,
        string IdentityProvider,
        string PortalClientId,
        string? CorrelationId)
    {
        public const int CurrentSchemaVersion = 1;
        public const string SessionEndedType = "session.ended";

        public static Payload From(SessionEndedEvent e) => new(
            CurrentSchemaVersion,
            SessionEndedType,
            e.EventId.ToString("D"),
            e.SessionId,
            e.Reason,
            e.Detail,
            PortalSessionId.FormatStartedAt(e.OccurredAt),
            e.SessionStartedAt is { } started ? PortalSessionId.FormatStartedAt(started) : null,
            HttpSessionEndedSender.IdentityProvider,
            e.ClientId,
            e.CorrelationId);
    }
}
