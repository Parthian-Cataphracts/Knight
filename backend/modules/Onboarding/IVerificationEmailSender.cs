namespace Onboarding;

/// <summary>
/// Sends the email that proves a self-service registrant controls their address.
///
/// A port, not a mail client: the onboarding module knows a verification is
/// outstanding, and the deployment knows where its dashboard lives and what the
/// message should say. Distinct from the account-invitation sender on purpose —
/// a self-service account already set its own password, so its link goes to the
/// verify page, never to a set-a-password page.
/// </summary>
public interface IVerificationEmailSender
{
    /// <summary>False when this deployment has no mail transport or no dashboard URL.</summary>
    bool CanSend { get; }

    /// <summary>
    /// Sends the link. The plaintext token appears here and nowhere else — not in
    /// the audit trail, not in a log line, and not in any API response.
    /// </summary>
    Task<bool> SendAsync(string email, string displayName, string verificationToken, CancellationToken cancellationToken);
}
