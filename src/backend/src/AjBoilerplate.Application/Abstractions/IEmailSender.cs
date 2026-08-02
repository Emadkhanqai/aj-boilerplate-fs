namespace AjBoilerplate.Application.Abstractions;

/// <summary>A file attached to an outgoing email.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Sends an email, optionally with one attachment. Implemented by an SMTP transport in the cloud and
/// a logging no-op locally, so an application flow that notifies someone is dispatch-agnostic.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, EmailAttachment? attachment, CancellationToken cancellationToken);
}
