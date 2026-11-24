using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Events;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Endpoints;

/// <summary>
/// The internal admin surface a trusted caller (an admin console, another service) uses to manage who holds which seat, and to make
/// open sessions notice a change.
/// </summary>
/// <remarks>
/// <para>
/// <c>/internal/</c>, not <c>/auth/</c>, and that is the whole security design of the prefix: the gateway proxies <c>/auth/</c> to this
/// service and nothing else, so these paths are unreachable from a browser BY ROUTING rather than by a deny rule. On top of that every
/// route needs the caller to authenticate: the <c>X-Admin-Api-Key</c> header, or (opt-in) a Google ID token from an allowed caller.
/// </para>
/// <para>
/// The endpoints are declarative: the caller posts the membership it wants and a repeat of the same request changes nothing. That
/// keeps a retry after a timeout safe, which matters because the caller here is another service, not a person clicking once.
/// </para>
/// </remarks>
internal static class AdminEndpoints
{
    public const string ApiKeyHeader = "X-Admin-Api-Key";
    private const int MaxSubjectLength = 256;

    /// <summary>The body of the "holds no seat" 404, byte for byte what the portal API matches on.</summary>
    internal const string NoSeatMessage = "that subject holds no seat in any portal this installation administers";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/internal/admin").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var options = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.Admin;

            // Nothing configured to authenticate a caller with: the surface does not exist, rather than existing half-wired.
            if (!options.IsEnabled)
            {
                return Results.NotFound();
            }

            var keyAccepted = !string.IsNullOrWhiteSpace(options.ApiKey) && KeyMatches(http.Request.Headers[ApiKeyHeader].ToString(), options.ApiKey);
            if (!keyAccepted)
            {
                var verifier = http.RequestServices.GetRequiredService<GoogleIdTokenVerifier>();
                if (!await verifier.IsAdminCallerAuthorizedAsync(http.Request.Headers.Authorization, http.RequestAborted))
                {
                    return Results.Unauthorized();
                }
            }

            http.Response.Headers.CacheControl = "no-store";

            // A subject names a user in the URL; nothing legitimate is longer than this, and it is not sent upstream.
            if (http.Request.RouteValues["subject"] is string subject && (string.IsNullOrWhiteSpace(subject) || subject.Length > MaxSubjectLength))
            {
                return Results.BadRequest(new { error = "subject is required (at most 256 characters)" });
            }

            return await next(context);
        });

        admin.MapGet("/users", SearchUsersAsync);
        admin.MapPost("/users", CreateUserAsync);
        admin.MapGet("/groups", GetGroupTreeAsync);
        admin.MapGet("/members", GetPortalMembersAsync);
        admin.MapGet("/users/{subject}/groups", GetGroupsAsync);
        admin.MapPut("/users/{subject}/groups", SetGroupsAsync);
        admin.MapPost("/users/{subject}/disable", DisableAsync);
        admin.MapPost("/users/{subject}/enable", EnableAsync);
        admin.MapPost("/users/{subject}/refresh", RefreshAsync);
        return app;
    }

    /// <summary>Constant time, and independent of the supplied key's length: both sides are hashed first.</summary>
    internal static bool KeyMatches(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    /// <summary>Directory search by name, username or e-mail. A blank term answers an empty list.</summary>
    private static async Task<IResult> SearchUsersAsync(
        [FromQuery] string? q, UserAdminService admin, CancellationToken cancellationToken, [FromQuery] int? limit = null)
    {
        var (outcome, users) = await admin.SearchUsersAsync(q ?? string.Empty, limit ?? 25, cancellationToken);
        return outcome == AdminOutcome.Success
            ? Results.Ok(new UsersResponse(users))
            : Respond(AdminResult.Failed(outcome), _ => Results.Ok());
    }

    /// <summary>
    /// Grants a seat, creating the account if the realm does not have one. Not declarative, so not safely repeatable in general, but
    /// the underlying add IS idempotent, so a retry after a timeout grants the same seat rather than a second one. Keyed by e-mail
    /// because a person who has never signed in has no subject to key by.
    /// </summary>
    private static async Task<IResult> CreateUserAsync(
        [FromBody] CreateUserRequest request, UserAdminService admin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request.GroupPath))
        {
            return Results.BadRequest(new { error = "email and groupPath are required" });
        }

        var result = await admin.CreateUserAsync(
            new NewUser(request.Email, request.FirstName ?? string.Empty, request.LastName ?? string.Empty, request.GroupPath),
            cancellationToken);

        return Respond(result, r => r.User is null
            ? Results.Problem(detail: "The grant was accepted but neither the read-back nor the write response yielded a subject, so there is no record to return. Retry; the add is idempotent.", statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new CreatedUserResponse(r.User, r.Groups ?? [], r.Added ?? [], r.Created)));
    }

    /// <summary>
    /// The list an administration screen opens on: who holds a seat in one of OUR portals.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>portals</c> is not decoration and a caller that drops it will publish a falsehood. The
    /// upstream key is scoped per group, so a portal it may not read answers 403 — and a 403 folded
    /// into "no members" turns the screen's <i>"Nobody has access to these portals yet"</i> into a
    /// confident false statement about who holds access, produced by a permission error. The
    /// <c>members</c> array covers the portals whose row reads <c>Success</c> and no others.
    /// </remarks>
    private static async Task<IResult> GetPortalMembersAsync(UserAdminService admin, CancellationToken cancellationToken)
    {
        var view = await admin.GetPortalMembersAsync(cancellationToken);
        return view.Outcome == AdminOutcome.Success
            ? Results.Ok(new MembersResponse(view.Members, Coverage(view.Portals)))
            : Respond(AdminResult.Failed(view.Outcome), _ => Results.Ok());
    }

    /// <summary>The managed portals and their seats. <c>portals</c> as for the members listing.</summary>
    private static async Task<IResult> GetGroupTreeAsync(UserAdminService admin, CancellationToken cancellationToken)
    {
        var view = await admin.GetGroupTreeAsync(cancellationToken);
        return view.Outcome == AdminOutcome.Success
            ? Results.Ok(new GroupTreeResponse(view.Groups, Coverage(view.Portals)))
            : Respond(AdminResult.Failed(view.Outcome), _ => Results.Ok());
    }

    private static async Task<IResult> GetGroupsAsync(
        string subject, UserAdminService admin, CancellationToken cancellationToken) =>
        Respond(
            await admin.GetGroupsAsync(subject, cancellationToken),
            r => Results.Ok(new GroupsResponse(r.Groups ?? [], Coverage(r.Portals))));

    /// <summary>
    /// Declarative membership. <c>added</c> and <c>removed</c> report what the UPSTREAM CONFIRMED.
    /// </summary>
    /// <remarks>
    /// The upstream has no single declarative call, so this is N adds and M removes and a fault
    /// half-way through is reachable. Where that happens the failure is returned with
    /// <c>added</c>, <c>removed</c> and <c>groups</c> as ProblemDetails EXTENSIONS, carrying what
    /// actually landed — a failure that reported them empty would be a write this service performed
    /// and denied performing, and the caller's next read would contradict its own screen.
    /// </remarks>
    private static async Task<IResult> SetGroupsAsync(
        string subject, [FromBody] GroupsRequest request, UserAdminService admin, CancellationToken cancellationToken)
    {
        if (request?.Groups is null)
        {
            return Results.BadRequest(new { error = "groups is required" });
        }

        var result = await admin.SetGroupsAsync(subject, request.Groups, cancellationToken);
        return Respond(result, r => Results.Ok(new GroupChangeResponse(
            r.Groups ?? [], r.Added ?? [], r.Removed ?? [], Coverage(r.Portals), Held(r.Withheld))));
    }

    private static async Task<IResult> DisableAsync(
        string subject, UserAdminService admin, CancellationToken cancellationToken) =>
        Respond(await admin.DisableAsync(subject, cancellationToken), _ => Results.NoContent());

    private static async Task<IResult> EnableAsync(
        string subject, UserAdminService admin, CancellationToken cancellationToken) =>
        Respond(await admin.EnableAsync(subject, cancellationToken), _ => Results.NoContent());

    /// <summary>
    /// Flags the user's sessions without changing anything in Keycloak — for a change made by
    /// another route (an admin editing the group in Keycloak's own console, say) that should still
    /// converge now rather than on the next sign-in.
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        string subject, ForceRefreshHandler handler, CancellationToken cancellationToken)
    {
        var flagged = await handler.HandleAsync(subject, cancellationToken);
        return Results.Ok(new RefreshResponse(flagged));
    }

    /// <summary>
    /// Maps an outcome to a status the caller can act on. A transient failure is 503, never 500: the caller is a service, and "try
    /// again" is the correct and machine-readable answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>_ =&gt;</c> arm is deliberate and must stay: a new <see cref="AdminOutcome"/> member defaults to a refusal rather than
    /// to anything that grants access.
    /// </para>
    /// <para>
    /// The problem details NAME NO SINGLE CAUSE where one status stands for several: a classifier that folds several upstream
    /// statuses into one outcome cannot also carry the cause in its message, so they point at the log, where
    /// <c>KeycloakAdminClient</c> keeps the operation and the upstream status distinct.
    /// </para>
    /// </remarks>
    private static IResult Respond(AdminResult result, Func<AdminResult, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result.Outcome switch
        {
            AdminOutcome.Success => onSuccess(result),

            // The words are a wire contract: the portal API tells THIS 404 ("holds no seat") from a switched-off surface's bare 404 by
            // matching them (AuthServiceIdentityAdministration.NoSeatMarker). Change one side and the other must follow.
            AdminOutcome.UserNotFound => Results.NotFound(new { error = NoSeatMessage }),

            // 422, a domain rule. One message for a malformed address, a domain that is not allowed and an empty allow-list, and it
            // names no domain, so it cannot be used to learn which domains are allowed. Nothing was sent to the directory.
            AdminOutcome.EmailDomainNotAllowed => Results.Json(
                new { error = "EMAIL_DOMAIN_NOT_ALLOWED", message = "This e-mail domain cannot be added." },
                statusCode: StatusCodes.Status422UnprocessableEntity),

            AdminOutcome.GroupNotFound => Results.BadRequest(new { error = "one or more group paths are not seats under a managed path; nothing was changed" }),

            // 501, and deliberately NOT an empty 200: an empty success would render as "no such person" or "nobody has access". A
            // capability that is not configured has to be said out loud.
            AdminOutcome.NotSupportedByUpstream => Results.Problem(
                detail: "The directory behind this surface is not configured (Auth:Admin:Keycloak:ClientId/ClientSecret) or has no equivalent of this operation. This is NOT a failure and NOT retryable until it is configured.",
                statusCode: StatusCodes.Status501NotImplemented),

            // Every managed path refused. Distinct from Denied because the remedy differs: the credential was ACCEPTED and is not
            // allowed to read these groups.
            AdminOutcome.PortalNotPermitted => Results.Problem(
                detail: "The upstream accepted our credential and refused every managed path: it is not allowed to read those groups. This is NOT an empty result and must not be rendered as 'nobody has access'. The log carries the operation and the upstream status.",
                statusCode: StatusCodes.Status403Forbidden),

            // Two causes land here and they have different remedies (a wrong or expired admin client credential: 401; a missing
            // realm-management role: 403), so the detail names both and sends the reader to the log, which tells them apart.
            AdminOutcome.Denied => Results.Problem(
                detail: "The upstream refused this admin call on our CREDENTIAL rather than on the request: either the admin client's credentials were not accepted (401) or its service account lacks a realm-management role for this call (403). The log carries the operation and the upstream status.",
                statusCode: StatusCodes.Status502BadGateway),

            // 502 rather than 503, because the distinction this carries is "a retry cannot change this".
            AdminOutcome.UpstreamRejected => Results.Problem(
                detail: "The upstream rejected this call on its own terms and a retry will NOT help: either the request this service built was invalid, or more than one directory account carries the e-mail it addressed, which only a person can resolve. The log carries the operation and the upstream status. The upstream's own validation text is deliberately not relayed: it can echo the request, including the e-mail address.",
                statusCode: StatusCodes.Status502BadGateway),

            _ => Partial(result, Results.Problem(
                detail: "The upstream did not answer this admin call successfully. Retry; the log carries the operation and the upstream status.",
                statusCode: StatusCodes.Status503ServiceUnavailable)),
        };
    }

    /// <summary>
    /// Attaches what a failed membership change ACTUALLY applied, where it applied anything.
    /// </summary>
    /// <remarks>
    /// ProblemDetails extensions rather than a new body shape, so the success shape this surface's
    /// callers are built against is untouched and the fields carry the same names they do there.
    /// A partial write reported with empty <c>added</c>/<c>removed</c> would be a write this
    /// service performed and denied performing — the caller's screen would show the old state and
    /// its next read would contradict it.
    /// </remarks>
    private static IResult Partial(AdminResult result, IResult failure)
    {
        if ((result.Added is not { Count: > 0 }) && (result.Removed is not { Count: > 0 }))
        {
            return failure;
        }

        return Results.Problem(
            detail: "The upstream did not answer this admin call successfully, and PART OF THE CHANGE WAS ALREADY APPLIED. The added/removed/groups fields below are what actually landed, not what was requested. Retrying the same declarative request is safe and converges.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["groups"] = result.Groups ?? [],
                ["added"] = result.Added ?? [],
                ["removed"] = result.Removed ?? [],
                ["portals"] = Coverage(result.Portals),
                ["withheld"] = Held(result.Withheld),
            });
    }

    /// <summary>Which managed portals the answer above actually covers; never omitted, never null.</summary>
    private static IReadOnlyList<PortalCoverage> Coverage(IReadOnlyList<PortalAccess>? portals) =>
        [.. (portals ?? []).Select(p => new PortalCoverage(p.Path, p.Outcome.ToString()))];

    /// <summary>
    /// Changes the service refused to issue. Always present, so an empty array means "nothing was
    /// withheld" rather than "this build does not report it".
    /// </summary>
    private static IReadOnlyList<HeldBack> Held(IReadOnlyList<WithheldChange>? withheld) =>
        [.. (withheld ?? []).Select(w => new HeldBack(w.Path, w.Portal, w.Outcome.ToString(), w.Requested.ToString()))];

    /// <param name="Path">The managed product group.</param>
    /// <param name="Outcome">
    /// <c>Success</c>, or the reason it could not be read — <c>PortalNotPermitted</c>,
    /// <c>Transient</c>, <c>Denied</c>. A caller rendering "who has access" must treat anything
    /// other than <c>Success</c> as UNKNOWN for that portal, never as empty.
    /// </param>
    private sealed record PortalCoverage(string Path, string Outcome);

    private sealed record UsersResponse(IReadOnlyList<RealmUser> Users);

    private sealed record GroupTreeResponse(IReadOnlyList<RealmGroup> Groups, IReadOnlyList<PortalCoverage> Portals);

    private sealed record MembersResponse(IReadOnlyList<PortalMember> Members, IReadOnlyList<PortalCoverage> Portals);

    private sealed record GroupsRequest(IReadOnlyCollection<string> Groups);

    private sealed record GroupsResponse(IReadOnlyCollection<string> Groups, IReadOnlyList<PortalCoverage> Portals);

    private sealed record CreateUserRequest(string? Email, string? FirstName, string? LastName, string? GroupPath);

    /// <param name="User">The person. <c>subject</c> is always real — it comes from the write.</param>
    /// <param name="Groups">Their full membership where it could be read, else the one seat granted.</param>
    /// <param name="Added">
    /// The seat if this call MOVED them into it, empty if they already held it. A grant of a seat
    /// somebody already has is a successful no-op, and saying otherwise tells the screen a change
    /// happened that did not.
    /// </param>
    /// <param name="Created">
    /// <c>true</c> a new account was provisioned, <c>false</c> an existing one was attached,
    /// <c>null</c> the upstream did not say. An administrator granting a first seat is being told
    /// two quite different things and this is the only field that distinguishes them.
    /// </param>
    private sealed record CreatedUserResponse(
        RealmUser User,
        IReadOnlyCollection<string> Groups,
        IReadOnlyCollection<string> Added,
        bool? Created);

    /// <param name="Groups">The membership that resulted. Includes anything withheld, because the
    /// user still holds it.</param>
    /// <param name="Added">Seats this call moved them into.</param>
    /// <param name="Removed">Seats this call moved them out of. A withheld path is NEVER here.</param>
    /// <param name="Portals">Which managed portals this answer covers.</param>
    /// <param name="Withheld">
    /// ⛔ Changes asked for and NOT ISSUED, because their portal could not be read. <b>A non-empty
    /// list means the caller is holding a different state than the one it requested</b>, and it is
    /// the one field here that cannot be skipped: <c>added</c> and <c>removed</c> being empty is
    /// also what a no-op change looks like.
    /// </param>
    private sealed record GroupChangeResponse(
        IReadOnlyCollection<string> Groups,
        IReadOnlyCollection<string> Added,
        IReadOnlyCollection<string> Removed,
        IReadOnlyList<PortalCoverage> Portals,
        IReadOnlyList<HeldBack> Withheld);

    /// <param name="Path">The seat that was not touched.</param>
    /// <param name="Portal">The managed portal it sits under.</param>
    /// <param name="Outcome">Why that portal could not be read.</param>
    /// <param name="Requested">
    /// <c>Remove</c> or <c>Add</c> — which direction the caller was asking for. Carried per entry
    /// rather than split across two lists so there is exactly ONE field to check.
    /// </param>
    private sealed record HeldBack(string Path, string? Portal, string Outcome, string Requested);

    private sealed record RefreshResponse(int Flagged);
}
