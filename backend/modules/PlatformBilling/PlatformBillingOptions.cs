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

    /// <summary>
    /// Stripe, the first real provider behind the abstraction. Off unless a secret
    /// key is set: a deployment with no Stripe configured keeps running on the
    /// simulated provider, and a deployment that sets it gets a real hosted
    /// checkout and a signature-verified webhook. Choosing Stripe is one option,
    /// not a commitment — it is a single adapter next to the simulated one, and a
    /// second provider is another adapter and nothing else
    /// (docs/self-service-saas-plan.md §11).
    /// </summary>
    public StripeOptions Stripe { get; set; } = new();
}

public sealed class StripeOptions
{
    /// <summary>The Stripe secret key (<c>sk_…</c>). Empty leaves the Stripe provider unregistered.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The webhook signing secret (<c>whsec_…</c>) a callback is verified against.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Where Stripe returns the browser after a paid or a cancelled checkout.</summary>
    public string SuccessUrl { get; set; } = "https://knight.example/portal?checkout=success";

    public string CancelUrl { get; set; } = "https://knight.example/portal/plans?checkout=cancelled";

    /// <summary>
    /// How far a webhook's timestamp may be from now before it is refused, in
    /// seconds. Stripe's own default is five minutes; it is what stops a captured
    /// callback being replayed a day later.
    /// </summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey);
}
