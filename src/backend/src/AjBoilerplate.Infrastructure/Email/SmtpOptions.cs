namespace AjBoilerplate.Infrastructure.Email;

/// <summary>Bound from the <c>Smtp</c> config section. An empty <see cref="Host"/> disables SMTP and
/// selects <see cref="LoggingEmailSender"/> instead.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>Used when <see cref="TimeoutSeconds"/> is missing or out of range.</summary>
    public const int DefaultTimeoutSeconds = 30;

    private const int MinTimeoutSeconds = 1;

    /// <summary>An upper bound, not a recommendation: no outbound email is worth pinning an HTTP
    /// request (and its DbContext scope) for longer than this.</summary>
    private const int MaxTimeoutSeconds = 120;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string From { get; set; } = "no-reply@example.invalid";

    public string? Username { get; set; }

    /// <summary>Never set in a committed file — supply it from the cloud secret store or the
    /// environment.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// How long one send attempt may take before it is failed as <c>SmtpTimeout</c>. Deliberately
    /// short: a send runs inside the caller's request scope, so an unbounded attempt keeps the
    /// request — and the scoped DbContext that would record the delivery outcome — alive
    /// indefinitely. A black-holed SMTP endpoint is exactly how that goes wrong in practice.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>
    /// <see cref="TimeoutSeconds"/> as a <see cref="TimeSpan"/>, clamped to a sane range so a typo
    /// (0, a negative, or an hour) cannot reintroduce an effectively unbounded send.
    /// </summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(
        TimeoutSeconds < MinTimeoutSeconds || TimeoutSeconds > MaxTimeoutSeconds
            ? DefaultTimeoutSeconds
            : TimeoutSeconds);
}
