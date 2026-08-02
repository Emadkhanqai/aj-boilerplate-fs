using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using AjBoilerplate.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Infrastructure.Email;

/// <summary>
/// Sends mail over SMTP, used when an SMTP host is configured. Credentials and host come from the
/// <c>Smtp</c> section (in the cloud, from the secret store) — never from committed files.
///
/// Every attempt is bounded by <see cref="SmtpOptions.Timeout"/> and every failure is translated
/// into an <see cref="EmailSendException"/> carrying a diagnostic failure code, because this sender
/// runs inside the caller's request scope: an unbounded attempt keeps that scope (and its DbContext)
/// alive, which is how a black-holed SMTP endpoint leaves delivery rows stuck with nothing recorded
/// and nothing logged.
///
/// Failure shapes observed from <see cref="SmtpClient"/> on .NET 10 (measured, not assumed — these
/// drive <see cref="Classify"/>):
/// <list type="bullet">
///   <item>refused connect → <see cref="SmtpException"/>, <c>GeneralFailure</c>, inner <see cref="System.Net.Sockets.SocketException"/></item>
///   <item>unknown host → <see cref="SmtpException"/>, <c>GeneralFailure</c>, inner <see cref="System.Net.Sockets.SocketException"/></item>
///   <item>connect/banner black hole → <see cref="SmtpException"/>, <c>GeneralFailure</c>, no inner, at <see cref="SmtpClient.Timeout"/></item>
///   <item>server answers <c>421</c>/<c>554</c> → <see cref="SmtpException"/> with that status code</item>
/// </list>
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    /// <summary>
    /// Extra headroom for the belt-and-braces bound in <see cref="SendWithinAsync"/>. Long enough
    /// that <see cref="SmtpClient.Timeout"/> always wins in practice (so the normal path produces the
    /// library's own, better-detailed exception), short enough that a request can never be pinned.
    /// </summary>
    private static readonly TimeSpan HardBoundGrace = TimeSpan.FromSeconds(5);

    private readonly SmtpOptions _options;

    public SmtpEmailSender(IOptions<SmtpOptions> options) => _options = options.Value;

    public async Task SendAsync(string to, string subject, string body, EmailAttachment? attachment, CancellationToken cancellationToken)
    {
        var timeout = _options.Timeout;

        // Bodies are authored as plain text and converted to minimal HTML here so links are clickable
        // in clients that do not auto-link bare URLs — see EmailBodyHtml.
        using var message = new MailMessage(_options.From, to, subject, EmailBodyHtml.From(body))
        {
            IsBodyHtml = true,
        };
        using var attachmentStream = attachment is null ? null : new MemoryStream(attachment.Content);
        if (attachment is not null && attachmentStream is not null)
        {
            message.Attachments.Add(new Attachment(attachmentStream, attachment.FileName, attachment.ContentType));
        }

        // Always negotiate TLS — mail contents must never traverse the network in clear text.
        // Timeout: verified on .NET 10 to bound SendMailAsync too (the docs only promise it for the
        // synchronous overloads), including the TCP-connect phase, which the CancellationToken
        // notably does NOT interrupt — hence both are applied, plus the hard bound below.
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = true,
            Timeout = (int)timeout.TotalMilliseconds,
        };
        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(timeout);

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await SendWithinAsync(client, message, timeout + HardBoundGrace, attemptCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own attempt timeout tripped, not the caller's cancellation.
            throw Timeout(timeout, ex);
        }
        catch (SmtpFailedRecipientsException ex)
        {
            throw Rejected($"SmtpRecipientRejected:{ex.InnerExceptions.FirstOrDefault()?.StatusCode ?? ex.StatusCode}", ex);
        }
        catch (SmtpFailedRecipientException ex)
        {
            throw Rejected($"SmtpRecipientRejected:{ex.StatusCode}", ex);
        }
        catch (SmtpException ex)
        {
            throw Classify(ex, Stopwatch.GetElapsedTime(startedAt), timeout);
        }
    }

    /// <summary>
    /// Awaits the send, but never for longer than <paramref name="hardBound"/>. Belt and braces: the
    /// primary bound is <see cref="SmtpClient.Timeout"/> (verified to apply to the async path on this
    /// runtime), and this guard exists only so that a runtime whose behaviour matches the documented
    /// "synchronous calls only" contract still cannot hang the caller forever. A send abandoned here
    /// is left to fault on its own once the disposed client tears its socket down; its exception is
    /// observed so it never surfaces as an unobserved task exception.
    /// </summary>
    private static async Task SendWithinAsync(SmtpClient client, MailMessage message, TimeSpan hardBound, CancellationToken cancellationToken)
    {
        var send = client.SendMailAsync(message, cancellationToken);
        using var boundCts = new CancellationTokenSource();
        var bound = Task.Delay(hardBound, boundCts.Token);

        if (await Task.WhenAny(send, bound).ConfigureAwait(false) == send)
        {
            await boundCts.CancelAsync().ConfigureAwait(false);
            await send.ConfigureAwait(false);
            return;
        }

        _ = send.ContinueWith(
            static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        throw new EmailTransportException(
            EmailTransportException.TimeoutCode,
            $"The SMTP send did not complete within {hardBound.TotalSeconds:F0}s and was abandoned.");
    }

    /// <summary>
    /// Splits "the server answered and refused" (actionable: credentials, sender identity, recipient)
    /// from "we never got a usable answer" (actionable: network path, endpoint, firewall), so the
    /// recorded <c>FailureCode</c> tells an operator which of the two to go and look at.
    /// </summary>
    private static EmailSendException Classify(SmtpException ex, TimeSpan elapsed, TimeSpan timeout)
    {
        if (ex.StatusCode != SmtpStatusCode.GeneralFailure)
        {
            return Rejected($"{EmailRejectedException.RejectedCode}:{ex.StatusCode}", ex);
        }

        // GeneralFailure is what System.Net.Mail reports for everything that failed below the SMTP
        // protocol itself. A socket-level inner exception means the endpoint answered fast and
        // negatively (refused/unresolvable); otherwise a GeneralFailure that consumed the whole
        // allowance is the library's own timeout.
        return ex.InnerException is null && elapsed >= timeout
            ? Timeout(timeout, ex)
            : new EmailTransportException(
                EmailTransportException.UnavailableCode,
                "The SMTP endpoint could not be reached.",
                ex);
    }

    private static EmailTransportException Timeout(TimeSpan timeout, Exception inner) =>
        new(EmailTransportException.TimeoutCode, $"The SMTP send timed out after {timeout.TotalSeconds:F0}s.", inner);

    // Messages stay transport-shaped on purpose: no recipient, no subject, no body, no credentials.
    private static EmailRejectedException Rejected(string failureCode, SmtpException ex) =>
        new(failureCode, "The SMTP server refused the message.", ex);
}
