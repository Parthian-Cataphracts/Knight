namespace Onboarding;

/// <summary>
/// Public self-service sign-up (docs/self-service-saas-plan.md §11.1, §12 phase
/// B). Turns an anonymous visitor into a customer account that can sign in once
/// it has proved control of its email address.
///
/// Two properties are load-bearing and enforced here rather than left to the
/// endpoint: registering must never reveal whether an email already has an
/// account (no existence oracle), and a freshly registered account cannot sign
/// in until it is verified.
/// </summary>
public interface IOnboardingService
{
    /// <summary>
    /// Registers a new customer and its owner account, unverified, and sends the
    /// verification link. Returns without error whether or not the email was
    /// already taken — the caller can never tell the two apart.
    /// </summary>
    Task RegisterAsync(string email, string password, string name, string? companyName, CancellationToken cancellationToken);

    /// <summary>
    /// Consumes a verification token. Returns true when it verified an account,
    /// false when the token is unknown or expired. Carries no email in either
    /// direction.
    /// </summary>
    Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Re-sends the verification link for an account that has not verified yet.
    /// Returns without error regardless, so it cannot be used to probe which
    /// emails have an unverified account.
    /// </summary>
    Task ResendVerificationAsync(string email, CancellationToken cancellationToken);
}
