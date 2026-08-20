using AccessControl;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// Turns an invitation into an email.
///
/// The wording and the link live here rather than in the access module, because
/// only the deployment knows where its dashboard is reachable from. The module
/// knows an invitation exists; this knows what an invitation looks like when it
/// arrives in somebody's inbox.
///
/// The mail is plain text on purpose. It carries one link and one deadline, it
/// has to survive every mail client anybody uses, and an HTML message with a
/// hidden link target is exactly the shape of the phishing this replaces.
/// </summary>
internal sealed class AccountInvitationSender : IAccountInvitationSender
{
    private readonly IEmailSender _email;
    private readonly ILogger<AccountInvitationSender> _logger;
    private readonly EmailOptions _options;
    private readonly AccessControlOptions _access;

    public AccountInvitationSender(
        IEmailSender email,
        ILogger<AccountInvitationSender> logger,
        IOptions<EmailOptions> options,
        IOptions<AccessControlOptions> access)
    {
        _email = email;
        _logger = logger;
        _options = options.Value;
        _access = access.Value;
    }

    /// <summary>
    /// Needs both a transport and somewhere to send people. A link to a
    /// dashboard KNIGHT cannot name is not an invitation.
    /// </summary>
    public bool CanSend => _email.IsConfigured && !string.IsNullOrWhiteSpace(_options.DashboardBaseUrl);

    public async Task<bool> SendAsync(ControlPlaneUser user, string activationToken, CancellationToken cancellationToken)
    {
        if (!CanSend)
        {
            return false;
        }

        var link = $"{_options.DashboardBaseUrl!.TrimEnd('/')}/activate?token={Uri.EscapeDataString(activationToken)}";
        var hours = (int)_access.InvitationLifetime.TotalHours;

        var body =
            $"""
             Hello {user.DisplayName},

             An account has been created for you on KNIGHT. Choose your password here:

             {link}

             The link works once and expires in {hours} hours. If it has expired, ask an
             administrator to send you a new one.

             If you were not expecting this, ignore this message — the account cannot be
             used until somebody follows the link.
             """;

        var result = await _email.SendAsync(
            new EmailMessage(user.Email, "Set up your KNIGHT account", body),
            cancellationToken);

        if (!result.Delivered)
        {
            // Logged without the link. The caller falls back to a one-time
            // password, so an undeliverable invitation is a worse experience
            // rather than an unusable account.
            _logger.LogWarning(
                "The invitation for account {UserId} could not be sent: {Error}",
                user.Id,
                result.Error);
        }

        return result.Delivered;
    }
}
