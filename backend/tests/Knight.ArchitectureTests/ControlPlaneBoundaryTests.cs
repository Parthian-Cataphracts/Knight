using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Knight.ArchitectureTests;

/// <summary>
/// KNIGHT is a control plane, never a store's business backend
/// (docs/README.md, rule 1). Phase 8 ported the store-side modules to Django and
/// deleted them from this solution; these tests are what stops them growing back
/// one convenient class at a time.
/// </summary>
public sealed class ControlPlaneBoundaryTests
{
    /// <summary>
    /// The store-side modules removed in phase 8, by root namespace. "Customer"
    /// here is the store's own end consumers — an entirely different concept from
    /// the control plane's paying "Customers" (docs/migration-plan.md,
    /// terminology), which is why the singular/plural distinction is load-bearing
    /// rather than sloppy naming.
    /// </summary>
    private static readonly string[] RemovedStoreModules =
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
        typeof(Servers.Domain.Server).Assembly,
        typeof(Plans.Domain.Plan).Assembly,
        typeof(Subscriptions.Domain.Subscription).Assembly,
        typeof(Billing.Domain.Invoice).Assembly,
    ];

    [Fact]
    public void ControlPlaneModules_ShouldNotDependOn_FrozenStoreModules()
    {
        foreach (var assembly in ControlPlaneModules)
        {
            foreach (var frozenModule in RemovedStoreModules)
            {
                var result = Types.InAssembly(assembly)
                    .Should()
                    .NotHaveDependencyOn(frozenModule)
                    .GetResult();

                Assert.True(
                    result.IsSuccessful,
                    $"{assembly.GetName().Name} depends on the removed store module '{frozenModule}': {Describe(result)}");
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
            [typeof(Servers.Domain.Server).Assembly] = "Servers",
            [typeof(Observability.Domain.ErrorGroup).Assembly] = "Observability",
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

    /// <summary>
    /// The removal itself, asserted.
    ///
    /// A store's catalogue, orders, checkout and payments belong to the store's
    /// own Django application and its own database. Re-adding any of them here —
    /// even "just a small read model, for the dashboard" — would put the control
    /// plane back in the business-backend role that ADR 0023 spent a phase
    /// getting it out of. The failure message is deliberately blunt, because the
    /// person who trips this will be midway through something that felt
    /// reasonable.
    /// </summary>
    [Fact]
    public void StoreBusinessDomains_ShouldNotExist_InTheControlPlane()
    {
        var assemblies = ControlPlaneModules
            .Append(typeof(Knight.Infrastructure.ControlPlane.ControlPlaneInfrastructure).Assembly)
            .Append(typeof(Program).Assembly)
            .Distinct();

        foreach (var assembly in assemblies)
        {
            foreach (var removed in RemovedStoreModules)
            {
                var offenders = assembly.GetTypes()
                    .Where(type => type.Namespace is not null)
                    .Where(type => type.Namespace == removed || type.Namespace!.StartsWith(removed + ".", StringComparison.Ordinal))
                    .Select(type => type.FullName)
                    .ToArray();

                Assert.True(
                    offenders.Length == 0,
                    $"'{removed}' is a store business domain and was removed from the control plane in phase 8. "
                        + $"{assembly.GetName().Name} defines it again: {string.Join(", ", offenders!)}. "
                        + "It belongs in the store's Django application, not here.");
            }
        }
    }

    private static string Describe(TestResult result) =>
        result.FailingTypes is null
            ? "No details available."
            : string.Join(", ", result.FailingTypes.Select(type => type.FullName));
}
