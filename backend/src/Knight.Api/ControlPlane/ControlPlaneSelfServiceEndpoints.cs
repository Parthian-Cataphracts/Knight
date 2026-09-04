using Knight.Contracts.ControlPlane;
using PlatformBilling;
using PlatformBilling.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The self-service SaaS surface (docs/self-service-saas-plan.md §6): the public
/// price list, the customer-owner checkout, and the provider webhook that is the
/// only path that activates a paid subscription.
///
/// Kept apart from <see cref="ControlPlaneBillingEndpoints"/>, which is the
/// operator's invoice-centric billing — the two billing domains never merge (§3).
/// </summary>
public static class ControlPlaneSelfServiceEndpoints
{
    /// <summary>The header a provider signs its webhook body with.</summary>
    private const string SignatureHeader = "X-Knight-Signature";

    public static void MapControlPlaneSelfServiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // --- Public catalogue ------------------------------------------------
        endpoints.MapGet("/api/v1/plans", async (
            IPublicPlanCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            var plans = await catalog.ListAsync(cancellationToken);
            return Results.Ok(plans.Select(ToResponse).ToArray());
        }).AllowAnonymous().RequireRateLimiting("control-plane").WithTags("Self-Service");

        // --- Checkout --------------------------------------------------------
        endpoints.MapPost("/api/v1/billing/checkout", async (
            CheckoutRequestBody request,
            IControlPlanePrincipal principal,
            ICheckoutService checkout,
            CancellationToken cancellationToken) =>
        {
            // Only a customer-scoped owner buys, and only for their own customer:
            // the customer id comes from the principal, never from the body.
            if (principal.CustomerId is not { } customerId)
            {
                return Results.Problem(
                    title: "Only a customer account can check out.",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "UNAUTHORIZED_STORE_ACCESS" });
            }

            var interval = string.Equals(request.BillingInterval, "yearly", StringComparison.OrdinalIgnoreCase)
                ? BillingInterval.Yearly
                : BillingInterval.Monthly;

            var result = await checkout.CheckoutAsync(
                new CheckoutRequest(
                    customerId,
                    request.PlanId,
                    interval,
                    request.SelectedFeatureIds ?? [],
                    request.Provider),
                cancellationToken);

            return Results.Ok(new CheckoutResponse
            {
                CheckoutSessionId = result.CheckoutSessionId,
                SubscriptionId = result.SubscriptionId,
                CheckoutUrl = result.CheckoutUrl,
                Amount = result.Amount,
                Currency = result.Currency,
            });
        }).RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
          .RequireRateLimiting("control-plane")
          .WithTags("Self-Service");

        // --- Provider webhook ------------------------------------------------
        // Anonymous by nature: it is authenticated by the provider's signature,
        // not by a KNIGHT session. The raw body is read verbatim because the
        // signature is over the exact bytes, not a re-serialised object.
        endpoints.MapPost("/api/v1/billing/webhooks/{provider}", async (
            string provider,
            HttpContext http,
            IPlatformWebhookService webhooks,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var payload = await reader.ReadToEndAsync(cancellationToken);
            var signature = http.Request.Headers[SignatureHeader].ToString();

            var result = await webhooks.HandleAsync(provider, payload, signature, cancellationToken);

            return result.Outcome switch
            {
                WebhookOutcome.Processed or WebhookOutcome.AlreadyProcessed or WebhookOutcome.Ignored =>
                    Results.Ok(new { status = result.Outcome.ToString().ToLowerInvariant(), subscriptionId = result.SubscriptionId }),

                WebhookOutcome.InvalidSignature => Results.Problem(
                    title: "The webhook signature did not verify.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "unauthorized" }),

                WebhookOutcome.Malformed => Results.Problem(
                    title: "The webhook body was not recognised.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "validation_failed" }),

                // An unknown session is acknowledged, not retried: a provider that
                // sends events for a checkout KNIGHT never opened should not have
                // them redelivered forever.
                _ => Results.Ok(new { status = "ignored" }),
            };
        }).AllowAnonymous().RequireRateLimiting("control-plane").WithTags("Self-Service");
    }

    private static PublicPlanResponse ToResponse(PublicPlan plan) => new()
    {
        Id = plan.Id,
        Key = plan.Key,
        Name = plan.Name,
        Description = plan.Description,
        BasePrice = plan.BasePrice,
        Currency = plan.Currency,
        IncludedFeatures = plan.IncludedFeatures
            .Select(feature => new PublicFeatureResponse
            {
                FeatureId = feature.FeatureId,
                Slug = feature.Slug,
                Name = feature.Name,
                Description = feature.Description,
            })
            .ToArray(),
        OptionalFeatures = plan.OptionalFeatures
            .Select(feature => new PublicOptionalFeatureResponse
            {
                FeatureId = feature.FeatureId,
                Slug = feature.Slug,
                Name = feature.Name,
                Description = feature.Description,
                Price = feature.Price,
                Currency = feature.Currency,
            })
            .ToArray(),
    };
}
