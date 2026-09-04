using System.ComponentModel.DataAnnotations;

namespace PlatformBilling;

/// <summary>
/// Configuration for KNIGHT's own self-service billing (docs/self-service-saas-plan.md §11).
/// The real provider and its credentials arrive with the product-owner's payment
/// choice; until then the simulated provider needs only a checkout base URL and,
/// optionally, a webhook secret.
/// </summary>
public sealed class PlatformBillingOptions
{
    public const string SectionName = "PlatformBilling";

    /// <summary>
    /// The provider a checkout is opened with when the caller does not name one.
    /// Defaults to the simulated provider so a fresh deployment runs the whole
    /// journey without a real gateway configured.
    /// </summary>
    public string DefaultProvider { get; set; } = "simulated";

    /// <summary>
    /// Where the simulated provider sends the browser to "pay". A stand-in for a
    /// real gateway's hosted page; the query carries the provider session id so a
    /// developer or an acceptance test can post the matching webhook back.
    /// </summary>
    [Required]
    public string CheckoutBaseUrl { get; set; } = "https://checkout.simulated.local/pay";

    /// <summary>
    /// The shared secret a webhook is signed with. When empty the simulated
    /// provider accepts unsigned callbacks — a development convenience that a real
    /// provider never permits, and that is why the option exists to be set.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>How long a checkout session may sit unpaid before it expires.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(1);
}
