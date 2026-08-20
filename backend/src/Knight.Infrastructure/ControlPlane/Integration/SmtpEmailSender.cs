using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// Outbound mail, bound from the "Email" section.
///
/// Off unless a host is configured. A deployment with no mail server is a
/// perfectly ordinary state — every environment before the first production one
/// is in it — and the account creation path stays usable there by falling back
/// to the one-time password it always had.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>SMTP host. Empty means this deployment sends no mail, and everything that would have sent some says so instead.</summary>
    public string? Host { get; init; }

    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    /// <summary>
    /// Whether to upgrade the connection with STARTTLS. On by default: mail
    /// carrying an activation link is mail carrying a credential, and sending it
    /// in the clear across a network would defeat the point of not reading the
    /// password out over the phone.
    /// </summary>
    public bool UseStartTls { get; init; } = true;

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>The envelope sender. Required when a host is set — a message with no From is refused by most receivers.</summary>
    public string? FromAddress { get; init; }

    public string FromName { get; init; } = "KNIGHT";

    /// <summary>
    /// Where the dashboard is reachable, used to build the links in the mail.
    /// KNIGHT cannot infer this: the request that triggers an invitation may
    /// arrive on an internal address, and the person receiving the mail is not
    /// on that network.
    /// </summary>
    public string? DashboardBaseUrl { get; init; }

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>
/// SMTP delivery.
///
/// Uses the framework's own client rather than adding a mail library. What
/// KNIGHT sends is a handful of short, transactional messages — an activation
/// link, an alert notification — with no attachments, no templating engine and
/// no bulk sending, and every one of them is delivered to a mail server that
/// does the actual work. A dependency would buy features this does not use, on
/// the path that carries account activation links.
///
/// A failure is reported, never swallowed: the caller decides whether to fall
/// back, and the account-creation path does exactly that.
/// </summary>
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly EmailOptions _options;

    public SmtpEmailSender(ILogger<SmtpEmailSender> logger, IOptions<EmailOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            // Permanent, and honest. Nothing about this deployment will make the
            // message arrive, so a caller retrying it is a caller wasting time.
            return EmailSendResult.Permanent("No mail transport is configured on this host.");
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(_options.FromAddress!, _options.FromName),
            Subject = message.Subject,
            Body = message.TextBody,
            IsBodyHtml = false,
        };

        mail.To.Add(message.To);

        if (message.HtmlBody is { } html)
        {
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, null, "text/html"));
        }

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseStartTls,
            Timeout = (int)_options.Timeout.TotalMilliseconds,
        };

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        try
        {
            await client.SendMailAsync(mail, cancellationToken);
            return EmailSendResult.Success;
        }
        catch (SmtpFailedRecipientException exception)
        {
            // The address itself is wrong. Retrying delivers it to the same
            // nonexistent mailbox tomorrow.
            _logger.LogWarning(exception, "Mail to {Recipient} was refused by the server.", Mask(message.To));
            return EmailSendResult.Permanent(exception.Message);
        }
        catch (SmtpException exception)
        {
            _logger.LogWarning(exception, "Mail to {Recipient} could not be delivered.", Mask(message.To));

            return exception.StatusCode is SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxNameNotAllowed
                ? EmailSendResult.Permanent(exception.Message)
                : EmailSendResult.Transient(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            return EmailSendResult.Permanent(exception.Message);
        }
    }

    /// <summary>
    /// Logs a recipient without writing a full address into every log line. The
    /// domain is the part that matters when diagnosing delivery; the local part
    /// is somebody's identity.
    /// </summary>
    private static string Mask(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"***{address[at..]}";
    }
}
