namespace AjBoilerplate.Auth.Observability;

/// <summary>
/// The current request's correlation id (nginx's <c>X-Request-Id</c>, sanitised, else Kestrel's
/// trace identifier), set by the request middleware in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// Ambient rather than threaded through every signature because it is needed in exactly one place
/// far from HTTP: the session-ended event a request causes carries it, so a portal's history row
/// can be joined to this service's log line for the same request. Core stays free of anything
/// HTTP. Null outside a request (the expired-session sweeper), which is the truth: no request ended
/// that session.
/// </remarks>
public static class RequestCorrelation
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }
}
