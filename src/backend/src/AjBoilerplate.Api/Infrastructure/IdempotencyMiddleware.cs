using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AjBoilerplate.Application.Idempotency;
using AjBoilerplate.Application.Identity;
using AjBoilerplate.Contracts.Common;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Makes a retried unsafe request safe. A client that labels a <c>POST</c> with an
/// <c>Idempotency-Key</c> header gets an at-most-once guarantee: the first request executes and its
/// response is stored, and every later request carrying the same key returns that stored response
/// instead of creating a second record.
///
/// <para>
/// <b>Why a middleware rather than an action filter.</b> What must be replayed is the exact bytes the
/// first caller received — envelope, status code, and all. A filter sees an <c>IActionResult</c>
/// BEFORE <see cref="EnvelopeResultFilter"/> has wrapped it and before the formatter has serialised
/// it, so replaying from there would mean re-running that pipeline and hoping it produces the same
/// output. Buffering the response stream captures the finished artefact instead, which is the only
/// version of "the same response" that is actually verifiable.
/// </para>
///
/// <para>
/// <b>Scope is the authenticated caller, never the key alone.</b> Two users independently choosing
/// the key <c>1</c> is an ordinary client bug; letting them share a record would leak one user's
/// response body to the other. Because the scope is taken from the principal, an unauthenticated
/// request is passed straight through untouched — there is no identity to scope it to, and every
/// controller here requires authentication anyway, so authorization rejects it a moment later.
/// </para>
///
/// <para>
/// <b>Only successful responses are stored.</b> A 4xx or 5xx releases the claim, so the key stays
/// usable and a genuine retry genuinely retries. Storing a failure would freeze it: a request that
/// failed once on a transient fault could never be re-attempted under the same key, which is exactly
/// the key a well-behaved client retries with.
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    /// <summary>The request header a client sets to make its request replay-safe.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Set on a response served from storage rather than executed, so a client (and a
    /// support engineer reading a HAR file) can tell the two apart.</summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";

    /// <summary>
    /// The longest key accepted, restated here rather than referenced from the Domain.
    ///
    /// <c>AjBoilerplate.Api</c> must not reference <c>AjBoilerplate.Domain</c> — the rule
    /// <c>DependencyRuleTests.Api_does_not_reference_domain_directly</c> enforces — so
    /// <c>IdempotencyRecord.MaxKeyLength</c> is genuinely unreachable from here. Restating a constant
    /// is a drift risk, so it is not left to discipline: <c>IdempotencyKeyLimitTests</c> asserts the
    /// two are equal, and fails the build if anyone changes one without the other. A key longer than
    /// the column would otherwise be rejected by SQL Server as a 500 instead of by this middleware as
    /// a clean 400.
    /// </summary>
    public const int MaxKeyLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;
    private readonly IOptionsMonitor<IdempotencyOptions> _options;

    public IdempotencyMiddleware(
        RequestDelegate next, ILogger<IdempotencyMiddleware> logger, IOptionsMonitor<IdempotencyOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled || !IsUnsafeMethod(context.Request.Method))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            // Opt-in by design. Requiring the header would break every existing client and turn a
            // reliability feature into a breaking change; a client that wants the guarantee asks for
            // it. See ADR-0009 for why this is not enforced globally.
            await _next(context);
            return;
        }

        var key = headerValues.ToString().Trim();
        if (!IsWellFormedKey(key))
        {
            await WriteEnvelopeAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"The {HeaderName} header must be 1–{MaxKeyLength} printable ASCII characters.",
                EnvelopeCodes.ValidationError);
            return;
        }

        var claims = context.RequestServices.GetRequiredService<IActorClaims>();
        if (!ActorIdentifiers.IsRealUser(claims.Id))
        {
            // Nothing to scope the key to. Passing through lets [Authorize] answer 401, which is the
            // honest reply; inventing a shared "anonymous" scope would let unrelated callers collide
            // and read each other's stored responses.
            await _next(context);
            return;
        }

        await HandleAsync(context, claims.Id.Trim(), key, options);
    }

    private async Task HandleAsync(HttpContext context, string scope, string key, IdempotencyOptions options)
    {
        var idempotency = context.RequestServices.GetRequiredService<IIdempotencyService>();
        var path = context.Request.Path.Value ?? "/";
        var requestHash = await ComputeRequestHashAsync(context);

        var claim = await idempotency.BeginAsync(
            scope, key, context.Request.Method, path, requestHash, context.RequestAborted);

        switch (claim.Decision)
        {
            case IdempotencyDecision.Replay:
                await ReplayAsync(context, claim);
                return;

            case IdempotencyDecision.InProgress:
                await WriteEnvelopeAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "A request with this Idempotency-Key is still being processed. Retry shortly.",
                    EnvelopeCodes.Conflict);
                return;

            case IdempotencyDecision.RequestMismatch:
                await WriteEnvelopeAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "This Idempotency-Key was already used for a different request.",
                    EnvelopeCodes.Conflict);
                return;

            case IdempotencyDecision.Proceed:
            default:
                await ExecuteAndCaptureAsync(context, idempotency, scope, key, options);
                return;
        }
    }

    /// <summary>
    /// Runs the request with the response redirected into a buffer, then stores the buffer and copies
    /// it to the real body.
    ///
    /// The original stream is restored in a <c>finally</c>, and the buffer is copied out on EVERY
    /// path including an exception — the exception handler registered upstream in <c>Program.cs</c>
    /// writes its 500 to whatever stream is current, so failing to restore would send the error into
    /// a discarded buffer and the client would get an empty body.
    /// </summary>
    private async Task ExecuteAndCaptureAsync(
        HttpContext context, IIdempotencyService idempotency, string scope, string key, IdempotencyOptions options)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        catch
        {
            // The claim must not outlive a request that never produced a response, or the key is
            // burned and the client can never retry it.
            await AbandonQuietlyAsync(idempotency, scope, key);
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }

        var statusCode = context.Response.StatusCode;
        if (!IsStorable(statusCode))
        {
            // A failure releases the key rather than freezing it — a transient 500 must not make the
            // client's chosen key permanently unusable.
            await AbandonQuietlyAsync(idempotency, scope, key);
            return;
        }

        var body = buffer.ToArray();
        if (body.Length > options.EffectiveMaxResponseBytes)
        {
            // Refusing to store an oversized body would silently drop the guarantee — a later replay
            // would re-execute and double-write, which is the exact failure this feature prevents.
            // Fail loudly instead: the response the caller is receiving is still correct, and the
            // operator gets told the limit needs raising rather than discovering a duplicate record.
            _logger.LogError(
                "Response for Idempotency-Key '{Key}' is {Bytes} bytes, over the {Limit}-byte storage limit. " +
                "The key has been released, so a retry will EXECUTE AGAIN rather than replay. " +
                "Raise Idempotency:MaxResponseBytes or make this endpoint's response smaller.",
                key, body.Length, options.EffectiveMaxResponseBytes);
            await AbandonQuietlyAsync(idempotency, scope, key);
            return;
        }

        await idempotency.CompleteAsync(
            scope,
            key,
            statusCode,
            context.Response.ContentType,
            context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
            body,
            context.RequestAborted);
    }

    private static async Task ReplayAsync(HttpContext context, IdempotencyClaim claim)
    {
        context.Response.StatusCode = claim.StatusCode;
        context.Response.ContentType = claim.ContentType;

        // Location is restored because it is PART of the response being replayed, not decoration: a
        // 201 Created's Location is how the client reads back what it created, and a replay that
        // dropped it would hand a retrying client a materially different response from the one the
        // first attempt got — which is exactly what this mechanism promises not to do.
        //
        // No other header is restored. The rest are either recomputed correctly for this request by
        // the pipeline (the security headers, which run upstream of here) or would be actively
        // misleading if resurrected from an older request. Replaying every header would trade a real
        // bug for a subtler one.
        if (!string.IsNullOrEmpty(claim.Location))
        {
            context.Response.Headers.Location = claim.Location;
        }

        // Set explicitly: the stored body's length is authoritative, and any Content-Length left over
        // from the framework's own view of this (never-executed) request would be wrong.
        context.Response.ContentLength = claim.Body.Length;
        context.Response.Headers[ReplayedHeaderName] = "true";

        await context.Response.Body.WriteAsync(claim.Body, context.RequestAborted);
    }

    /// <summary>
    /// Releases a claim without letting the release itself break the request. The caller's response is
    /// already decided by this point; a failure to tidy up is an operational problem, not something to
    /// convert into a different reply than the one the work actually produced.
    /// </summary>
    private async Task AbandonQuietlyAsync(IIdempotencyService idempotency, string scope, string key)
    {
        try
        {
            await idempotency.AbandonAsync(scope, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not release the idempotency claim for key '{Key}'. It will block retries of this " +
                "key until the retention sweep removes it.",
                key);
        }
    }

    /// <summary>
    /// Digest of the request this key is being used for: method, path, and body.
    ///
    /// The body is read through <c>EnableBuffering</c> and rewound, so the model binder downstream
    /// still sees a readable stream from position zero. Without the rewind the action would bind an
    /// empty body — a failure that only appears once a key is actually supplied, which is precisely
    /// the path least likely to be exercised by hand.
    /// </summary>
    private static async Task<string> ComputeRequestHashAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var sha = SHA256.Create();
        using var payload = new MemoryStream();

        var prefix = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{context.Request.Method}\n{context.Request.Path}\n"));
        await payload.WriteAsync(prefix);
        await context.Request.Body.CopyToAsync(payload);
        context.Request.Body.Position = 0;

        payload.Position = 0;
        return Convert.ToHexString(await sha.ComputeHashAsync(payload));
    }

    private static bool IsUnsafeMethod(string method) =>
        HttpMethods.IsPost(method);

    /// <summary>Only a 2xx is worth replaying — see the class summary.</summary>
    private static bool IsStorable(int statusCode) => statusCode is >= 200 and < 300;

    /// <summary>
    /// Printable ASCII only, within the stored column's width. A key travels into a log line and into
    /// an error message, so control characters and unbounded length are rejected at the edge rather
    /// than truncated silently at the database.
    /// </summary>
    private static bool IsWellFormedKey(string key) =>
        key.Length is > 0 and <= MaxKeyLength
        && key.All(c => c is >= ' ' and <= '~');

    private static async Task WriteEnvelopeAsync(HttpContext context, int statusCode, string message, string code)
    {
        var body = ApiResponse.CreateError(message, code);
        body.TraceId = context.TraceIdentifier;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
