using AccessControl.Domain;

namespace AccessControl;

/// <summary>
/// Sends the invitation that lets a new administrator set their own password.
///
/// A port rather than a mail client, for two reasons. The module that decides
/// who may hold an account has no business knowing what SMTP is; and the wording
/// and the link belong to the deployment, which knows where its dashboard
/// actually is, while this module only knows that an invitation exists.
/// </summary>
public interface IAccountInvitationSender
{
    /// <summary>
    /// False when this deployment cannot send mail. Account creation branches on
    /// it up front rather than arming an invitation nobody will ever receive.
    /// </summary>
    bool CanSend { get; }

    /// <summary>
    /// Sends the link. The plaintext token appears here and nowhere else — not
    /// in the audit trail, not in a log line, and not in the response to the
    /// administrator who created the account.
    /// </summary>
    Task<bool> SendAsync(ControlPlaneUser user, string activationToken, CancellationToken cancellationToken);
}
