namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>One message KNIGHT wants to send. Plain text is required; HTML is an addition, never the only body.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

/// <summary>
/// Whether the message left KNIGHT.
///
/// <paramref name="IsPermanent"/> separates "this address will never work" from
/// "the mail server was busy": one is worth retrying and the other is worth
/// telling somebody about.
/// </summary>
public sealed record EmailSendResult(bool Delivered, string? Error, bool IsPermanent)
{
    public static readonly EmailSendResult Success = new(true, null, false);

    public static EmailSendResult Transient(string error) => new(false, error, false);

    public static EmailSendResult Permanent(string error) => new(false, error, true);
}

/// <summary>
/// Sends mail out of KNIGHT.
///
/// Exists because a new administrator should receive an activation link rather
/// than a password an operator reads out over the phone (TODO.md phase 9,
/// decided 2026-08-20). Notification channels of kind Email use it too.
///
/// The one rule: an implementation that cannot send must say so. A transport
/// that silently reports success would make the delivery log — the record of who
/// was told what — a lie exactly where it matters most.
/// </summary>
public interface IEmailSender
{
    /// <summary>False when this deployment has no mail transport configured. Callers branch on it rather than sending into a void.</summary>
    bool IsConfigured { get; }

    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
