namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// A failed outbound email send, carrying a short, stable <see cref="FailureCode"/> a delivery-log
/// row can persist.
///
/// The code exists so an operator can tell the two operationally different failures apart without
/// reading a stack trace: "we never got a usable answer from the mail server" (an infrastructure/
/// network problem — see <see cref="EmailTransportException"/>) versus "the mail server answered and
/// said no" (a credentials/relay/recipient problem — see <see cref="EmailRejectedException"/>).
/// Codes are deliberately transport-shaped, never message-shaped: they must never contain the
/// recipient, the subject, the body, or any credential.
/// </summary>
public abstract class EmailSendException : Exception
{
    protected EmailSendException(string failureCode, string message, Exception? innerException)
        : base(message, innerException) => FailureCode = failureCode;

    /// <summary>A short, stable, log-and-column-safe classification (e.g. <c>SmtpTimeout</c>).</summary>
    public string FailureCode { get; }
}

/// <summary>
/// The message never reached a mail server that could answer for it: DNS failure, refused or
/// black-holed TCP connect, TLS negotiation failure, or the configured send timeout elapsing.
/// Diagnostically this means "check the network path / the SMTP endpoint", not "check the message".
/// </summary>
public sealed class EmailTransportException : EmailSendException
{
    /// <summary>The send exceeded <c>Smtp:TimeoutSeconds</c>.</summary>
    public const string TimeoutCode = "SmtpTimeout";

    /// <summary>The endpoint could not be reached or the connection could not be established.</summary>
    public const string UnavailableCode = "SmtpTransportUnavailable";

    public EmailTransportException(string failureCode, string message, Exception? innerException = null)
        : base(failureCode, message, innerException)
    {
    }
}

/// <summary>
/// The mail server was reached and answered with a refusal (bad credentials, relay denied, sender or
/// recipient rejected, service unavailable). Diagnostically this means "check the account, the
/// sender identity, or the recipient", not "check the network".
/// </summary>
public sealed class EmailRejectedException : EmailSendException
{
    /// <summary>The server answered with an SMTP status code that refused the message.</summary>
    public const string RejectedCode = "SmtpRejected";

    public EmailRejectedException(string failureCode, string message, Exception? innerException = null)
        : base(failureCode, message, innerException)
    {
    }
}
