using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Onboarding;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// Turns an outstanding email verification into a message.
///
/// A sibling of <see cref="AccountInvitationSender"/>, and deliberately not the
/// same one: a self-service registrant already chose their password, so the link
/// goes to the verify page and the wording asks them to confirm an address, not
/// to set a credential they already have. Plain text on purpose, for the same
/// reasons — one link, one deadline, and nothing an HTML client can hide.
/// </summary>
internal sealed class VerificationEmailSender : IVerificationEmailSender
{
    private readonly IEmailSender _email;
    private readonly ILogger<VerificationEmailSender> _logger;
    private readonly EmailOptions _options;
    private readonly OnboardingOptions _onboarding;

    public VerificationEmailSender(
        IEmailSender email,
        ILogger<VerificationEmailSender> logger,
        IOptions<EmailOptions> options,
        IOptions<OnboardingOptions> onboarding)
    {
        _email = email;
        _logger = logger;
        _options = options.Value;
        _onboarding = onboarding.Value;
    }

    public bool CanSend => _email.IsConfigured && !string.IsNullOrWhiteSpace(_options.DashboardBaseUrl);

    public async Task<bool> SendAsync(string email, string displayName, string verificationToken, CancellationToken cancellationToken)
    {
        if (!CanSend)
        {
            return false;
        }

        var link = $"{_options.DashboardBaseUrl!.TrimEnd('/')}/verify-email?token={Uri.EscapeDataString(verificationToken)}";
        var hours = (int)_onboarding.EmailVerificationLifetime.TotalHours;

        var body =
            $"""
             Hello {displayName},

             Confirm your email address to finish creating your KNIGHT account:

             {link}

             The link works once and expires in {hours} hours. If it has expired, request
             a new one from the sign-in page.

             If you did not create an account, ignore this message — nothing was set up in
             your name that a confirmation would not be needed to use.
             """;

        var result = await _email.SendAsync(
            new EmailMessage(email, "Confirm your KNIGHT email address", body),
            cancellationToken);

        if (!result.Delivered)
        {
            _logger.LogWarning("A verification email could not be sent: {Error}", result.Error);
        }

        return result.Delivered;
    }
}
