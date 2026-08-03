namespace AjBoilerplate.Api.Infrastructure;

/// <summary>Bound from the <c>Idempotency</c> config section. Governs
/// <see cref="IdempotencyMiddleware"/>.</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>Used when <see cref="MaxResponseBytes"/> is missing or non-positive.</summary>
    public const int DefaultMaxResponseBytes = 256 * 1024;

    /// <summary>
    /// Whether an <c>Idempotency-Key</c> header is honoured at all. On by default: the mechanism is
    /// already opt-in per request (a client that sends no key is unaffected), so the switch exists for
    /// an operator who needs to disable it during an incident, not as an ordinary configuration
    /// choice. Turning it off means a retried create can double-write again.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The largest response body that will be stored for replay. A response over this size releases
    /// the key and logs an error rather than storing a truncated body — see
    /// <see cref="IdempotencyMiddleware"/>. Raise it rather than letting the guarantee lapse.
    /// </summary>
    public int MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    /// <summary><see cref="MaxResponseBytes"/> with a non-positive configured value falling back to
    /// <see cref="DefaultMaxResponseBytes"/>, so a typo (0, a negative) cannot disable storage
    /// entirely and quietly turn every replay back into a second execution.</summary>
    public int EffectiveMaxResponseBytes => MaxResponseBytes <= 0 ? DefaultMaxResponseBytes : MaxResponseBytes;

    /// <summary>How long a stored record stays useful, in hours. Nothing in the application enforces
    /// this — it is the window the retention sweep documented in <c>src/backend/README.md</c> should
    /// prune to, stated here so the number lives with the rest of the mechanism's configuration.</summary>
    public int RetentionHours { get; set; } = 24;
}
