using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Knight.ArchitectureTests;

/// <summary>
/// KNIGHT is a control plane, never a store's business backend
/// (docs/README.md, rule 1). The store-side modules are frozen and will be ported
/// to Django in phase 8; if a control-plane module ever reaches into one of them,
/// that removal stops being possible and the rule has already been broken in code.
/// </summary>
public sealed class ControlPlaneBoundaryTests
{
    /// <summary>
    /// The frozen store-side modules, by root namespace. "Customer" here is the
    /// store's own end consumers — an entirely different concept from the
    /// control plane's paying "Customers" (docs/migration-plan.md, terminology).
    /// </summary>
    private static readonly string[] FrozenStoreModules =
    [
        "Catalog", "Checkout", "Customer", "Delivery", "Fulfillment",
        "Ordering", "Payment", "Promotions", "Tenancy", "FeatureManagement",
    ];

    private static readonly Assembly[] ControlPlaneModules =
    [
        typeof(Customers.Domain.Customer).Assembly,
        typeof(Stores.Domain.Store).Assembly,
        typeof(AccessControl.Domain.ControlPlaneUser).Assembly,
        typeof(FeatureRegistry.Domain.Feature).Assembly,
        typeof(FeatureDelivery.Domain.FeatureInstallation).Assembly,
        typeof(Plans.Domain.Plan).Assembly,
        typeof(Subscriptions.Domain.Subscription).Assembly,
        typeof(Billing.Domain.Invoice).Assembly,
    ];

    [Fact]
    public void ControlPlaneModules_ShouldNotDependOn_FrozenStoreModules()
    {
        foreach (var assembly in ControlPlaneModules)
        {
            foreach (var frozenModule in FrozenStoreModules)
            {
                var result = Types.InAssembly(assembly)
                    .Should()
                    .NotHaveDependencyOn(frozenModule)
                    .GetResult();

                Assert.True(
                    result.IsSuccessful,
                    $"{assembly.GetName().Name} depends on the frozen store module '{frozenModule}': {Describe(result)}");
            }
        }
    }

    [Fact]
    public void ControlPlaneModules_ShouldNotDependOn_Infrastructure()
    {
        foreach (var assembly in ControlPlaneModules)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("Knight.Infrastructure")
                .GetResult();

            Assert.True(result.IsSuccessful, Describe(result));
        }
    }

    [Fact]
    public void ControlPlaneModules_ShouldNotDependOn_Api()
    {
        foreach (var assembly in ControlPlaneModules)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("Knight.Api")
                .GetResult();

            Assert.True(result.IsSuccessful, Describe(result));
        }
    }

    /// <summary>
    /// Modules stay independent of each other; anything they genuinely share —
    /// the audit trail, the customer scope, the secret factory — is a contract in
    /// the application layer, not a reference to a sibling.
    /// </summary>
    [Fact]
    public void ControlPlaneModules_ShouldNotDependOnEachOther()
    {
        var moduleNamespaces = new Dictionary<Assembly, string>
        {
            [typeof(Customers.Domain.Customer).Assembly] = "Customers",
            [typeof(Stores.Domain.Store).Assembly] = "Stores",
            [typeof(AccessControl.Domain.ControlPlaneUser).Assembly] = "AccessControl",
            [typeof(FeatureRegistry.Domain.Feature).Assembly] = "FeatureRegistry",
            [typeof(FeatureDelivery.Domain.FeatureInstallation).Assembly] = "FeatureDelivery",
            [typeof(Plans.Domain.Plan).Assembly] = "Plans",
            [typeof(Subscriptions.Domain.Subscription).Assembly] = "Subscriptions",
            [typeof(Billing.Domain.Invoice).Assembly] = "Billing",
        };

        foreach (var (assembly, ownNamespace) in moduleNamespaces)
        {
            foreach (var other in moduleNamespaces.Values.Where(name => name != ownNamespace))
            {
                var result = Types.InAssembly(assembly)
                    .Should()
                    .NotHaveDependencyOn(other)
                    .GetResult();

                Assert.True(result.IsSuccessful, $"{ownNamespace} depends on {other}: {Describe(result)}");
            }
        }
    }

    private static string Describe(TestResult result) =>
        result.FailingTypes is null
            ? "No details available."
            : string.Join(", ", result.FailingTypes.Select(type => type.FullName));
}
