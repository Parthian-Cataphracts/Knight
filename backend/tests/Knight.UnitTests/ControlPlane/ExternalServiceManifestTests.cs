using System.Text.Json;
using FeatureRegistry.Domain;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Reading a Feature that is a service rather than a package.
///
/// The second discriminator this manifest has, and it sits above the first:
/// <c>runtime</c> answers "what language is this code written for", and
/// <c>architecture</c> answers "is there code at all" (<c>adr/0033</c>).
///
/// Most of what is asserted here is refusal. A manifest is the last place an
/// author is present to fix a mistake, and every one of these fields becomes
/// either a route a shopper can reach or a request the store makes on its own
/// behalf — so a prefix that escapes its namespace, a method list that quietly
/// acquired a DELETE, or an origin carrying customer data over plain http are
/// all cheaper to refuse here than to find in production.
/// </summary>
public sealed class ExternalServiceManifestTests
{
    private static string Manifest(string body) => $$"""
        {
          "apiVersion": "knight.dev/v1",
          "slug": "subscriptions",
          "version": "2.0.0",
          "name": "Subscriptions",
          "architecture": "external_service",
          {{body}}
        }
        """;

    private const string Service = """
        "service": {
          "base_url": "https://subscriptions.knight.dev",
          "auth": "hmac-sha256",
          "health": "/healthz",
          "secret": "SUBSCRIPTIONS_SERVICE_SECRET"
        }
        """;

    private static FeatureManifest Parse(string body)
    {
        Assert.True(
            FeatureManifest.TryParse(Manifest(body), out var manifest, out var errors),
            string.Join("; ", errors));

        return manifest;
    }

    private static IReadOnlyList<ManifestError> Refuse(string body)
    {
        Assert.False(FeatureManifest.TryParse(Manifest(body), out _, out var errors));
        Assert.NotEmpty(errors);

        return errors;
    }

    // --- Backward compatibility, first --------------------------------------

    [Fact]
    public void AManifestWithNoArchitectureIsStillTheOldKind()
    {
        const string json = """
            {
              "apiVersion": "knight.dev/v1",
              "slug": "analytics-core",
              "version": "1.0.0",
              "name": "Analytics",
              "django": { "app_label": "analytics", "installed_app": "analytics" },
              "install": { "strategy": "package-install" }
            }
            """;

        Assert.True(FeatureManifest.TryParse(json, out var manifest, out var errors), string.Join("; ", errors));

        // Sixteen manifests were written before this field existed and every one
        // of them means in-process. Re-issuing them all to say so would be churn
        // that proves nothing, so absence means the old thing.
        Assert.Equal(FeatureArchitecture.InProcess, manifest.Architecture);
        Assert.False(manifest.IsExternalService);
        Assert.NotNull(manifest.Runtime);
        Assert.Null(manifest.External);
    }

    // --- The new shape ------------------------------------------------------

    [Fact]
    public void AnExternalFeatureHasAServiceAndNoRuntime()
    {
        var manifest = Parse($$"""
            {{Service}},
            "api_proxies": [ { "prefix": "subscriptions/", "upstream": "/api/v1/", "methods": ["GET", "POST"], "identity": "customer" } ]
            """);

        Assert.True(manifest.IsExternalService);

        // Null rather than a default, and the pairing is guaranteed in both
        // directions, so nothing downstream has to consider a third case.
        Assert.Null(manifest.Runtime);
        Assert.NotNull(manifest.External);
        Assert.Equal(new Uri("https://subscriptions.knight.dev"), manifest.External.Service.BaseUrl);
        Assert.Equal(ServiceAuthentication.HmacSha256, manifest.External.Service.Authentication);
        Assert.Equal("/healthz", manifest.External.Service.HealthPath);
    }

    [Fact]
    public void ItHasNoMigrationsBecauseItTouchesNoDatabase()
    {
        var manifest = Parse($$"""
            {{Service}},
            "webhooks": [ { "event": "order.placed", "path": "/hooks/order-placed" } ]
            """);

        // The single most important consequence of the whole architecture. There
        // is no schema in the store, so there is nothing to migrate, nothing to
        // reverse, and no maintenance window to ask for.
        Assert.False(manifest.Migrations.Required);
        Assert.Empty(manifest.Migrations.Extensions);
        Assert.Equal(InstallStrategy.NoOp, manifest.Install.Strategy);
        Assert.False(manifest.Install.RequiresRestart);
    }

    [Fact]
    public void WebhooksAreReadWithTheirDeliveryGuarantee()
    {
        var manifest = Parse($$"""
            {{Service}},
            "webhooks": [
              { "event": "order.placed", "path": "/hooks/order-placed", "delivery": "at-least-once" },
              { "event": "cart.abandoned", "path": "/hooks/cart-abandoned", "delivery": "at-most-once" }
            ]
            """);

        var webhooks = manifest.External!.Webhooks;

        Assert.Equal(2, webhooks.Count);
        Assert.Equal("order.placed", webhooks[0].Event);
        Assert.Equal(WebhookDelivery.AtLeastOnce, webhooks[0].Delivery);
        Assert.Equal(WebhookDelivery.AtMostOnce, webhooks[1].Delivery);
    }

    [Fact]
    public void ADeliveryGuaranteeDefaultsToTheSafeOne()
    {
        var manifest = Parse($$"""
            {{Service}},
            "webhooks": [ { "event": "order.placed", "path": "/hooks/order-placed" } ]
            """);

        // At-least-once, so the service must be idempotent. Defaulting the other
        // way would silently drop an event a customer was charged for.
        Assert.Equal(WebhookDelivery.AtLeastOnce, manifest.External!.Webhooks[0].Delivery);
    }

    [Fact]
    public void AProxyRouteIsReadOnlyUnlessItSaysOtherwise()
    {
        var manifest = Parse($$"""
            {{Service}},
            "api_proxies": [ { "prefix": "subscriptions/", "upstream": "/api/v1/" } ]
            """);

        // A route that acquires a DELETE because nobody wrote a method list is
        // exactly the failure this default exists to avoid.
        Assert.Equal(["GET"], manifest.External!.ApiProxies[0].Methods);
        Assert.Equal(ProxyIdentity.Anonymous, manifest.External.ApiProxies[0].Identity);
    }

    [Fact]
    public void UiMountsAreReadWithTheirSlotAndKind()
    {
        var manifest = Parse($$"""
            {{Service}},
            "ui_mounts": [ { "slot": "admin.sidebar", "label": "Subscriptions", "path": "/admin", "kind": "iframe" } ]
            """);

        var mount = Assert.Single(manifest.External!.UiMounts);

        Assert.Equal("admin.sidebar", mount.Slot);
        Assert.Equal("Subscriptions", mount.Label);
        Assert.Equal(UiMountKind.Iframe, mount.Kind);
    }

    // --- Refusals -----------------------------------------------------------

    [Fact]
    public void AnExternalFeatureCarryingARuntimeBlockIsRefused()
    {
        var errors = Refuse($$"""
            {{Service}},
            "django": { "app_label": "subs", "installed_app": "subs" },
            "webhooks": [ { "event": "order.placed", "path": "/hooks/order-placed" } ]
            """);

        // An author who has copied a manifest. Cheaper to say so at publish than
        // to deliver something the store will half-read.
        Assert.Contains(errors, error => error.Path == "$.django");
    }

    [Fact]
    public void AnExternalFeatureCarryingAMigrationsBlockIsRefused()
    {
        var errors = Refuse($$"""
            {{Service}},
            "migrations": { "required": true, "reversible": true },
            "webhooks": [ { "event": "order.placed", "path": "/hooks/order-placed" } ]
            """);

        // The most dangerous of the copied blocks, because it reads like a
        // promise that something will be migrated. Nothing will: the store never
        // gives this Feature a database handle.
        Assert.Contains(errors, error => error.Path == "$.migrations");
    }

    [Fact]
    public void AnExternalFeatureThatDoesNothingIsRefused()
    {
        var errors = Refuse(Service);

        // No events, no routes, no screens. A store would install it and never
        // notice, which is a manifest somebody has not finished.
        Assert.Contains(errors, error => error.Message.Contains("at least one of"));
    }

    [Fact]
    public void AServiceOnPlainHttpIsRefused()
    {
        var errors = Refuse("""
            "service": { "base_url": "ftp://subscriptions.knight.dev" },
            "webhooks": [ { "event": "order.placed", "path": "/hooks/order-placed" } ]
            """);

        Assert.Contains(errors, error => error.Path == "$.service.base_url");
    }

    [Fact]
    public void AProxyPrefixThatClimbsOutOfItsNamespaceIsRefused()
    {
        var errors = Refuse($$"""
            {{Service}},
            "api_proxies": [ { "prefix": "../admin/", "upstream": "/" } ]
            """);

        // A Feature claiming somewhere it was not given. The prefix becomes a
        // route in the store's own URL space.
        Assert.Contains(errors, error => error.Path.StartsWith("$.api_proxies[0].prefix"));
    }

    [Fact]
    public void AProxyPrefixClaimedTwiceIsRefused()
    {
        var errors = Refuse($$"""
            {{Service}},
            "api_proxies": [
              { "prefix": "subscriptions/", "upstream": "/a/" },
              { "prefix": "subscriptions/", "upstream": "/b/" }
            ]
            """);

        Assert.Contains(errors, error => error.Message.Contains("claimed twice"));
    }

    [Fact]
    public void AnEventSubscribedToTwiceIsRefused()
    {
        var errors = Refuse($$"""
            {{Service}},
            "webhooks": [
              { "event": "order.placed", "path": "/a" },
              { "event": "order.placed", "path": "/b" }
            ]
            """);

        // The store would deliver it twice to one service, which looks to the
        // service exactly like a retry and is not one.
        Assert.Contains(errors, error => error.Message.Contains("subscribed to twice"));
    }

    [Fact]
    public void AMethodNoStoreWillForwardIsRefused()
    {
        var errors = Refuse($$"""
            {{Service}},
            "api_proxies": [ { "prefix": "subscriptions/", "methods": ["GET", "TRACE"] } ]
            """);

        Assert.Contains(errors, error => error.Path.StartsWith("$.api_proxies[0].methods"));
    }

    [Fact]
    public void AnArchitectureKnightDoesNotKnowIsRefused()
    {
        const string json = """
            {
              "apiVersion": "knight.dev/v1",
              "slug": "subscriptions",
              "version": "2.0.0",
              "name": "Subscriptions",
              "architecture": "serverless_edge_wasm"
            }
            """;

        Assert.False(FeatureManifest.TryParse(json, out _, out var errors));
        Assert.Contains(errors, error => error.Path == "$.architecture");
    }
}
