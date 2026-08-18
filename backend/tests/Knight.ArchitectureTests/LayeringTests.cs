using NetArchTest.Rules;
using Xunit;

namespace Knight.ArchitectureTests;

public sealed class LayeringTests
{
    private const string DomainNamespace = "Knight.Domain";
    private const string ApplicationNamespace = "Knight.Application";
    private const string InfrastructureNamespace = "Knight.Infrastructure";
    private const string ApiNamespace = "Knight.Api";

    [Fact]
    public void Domain_ShouldNotDependOn_Application()
    {
        var result = Types.InAssembly(typeof(Knight.Domain.Common.Entity).Assembly)
            .Should()
            .NotHaveDependencyOn(ApplicationNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_ShouldNotDependOn_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Knight.Domain.Common.Entity).Assembly)
            .Should()
            .NotHaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_ShouldNotDependOn_AspNetCore()
    {
        var result = Types.InAssembly(typeof(Knight.Domain.Common.Entity).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Domain_ShouldNotDependOn_EntityFrameworkCore()
    {
        var result = Types.InAssembly(typeof(Knight.Domain.Common.Entity).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_ShouldNotDependOn_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Knight.Application.DependencyInjection).Assembly)
            .Should()
            .NotHaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_ShouldNotDependOn_EntityFrameworkCore()
    {
        var result = Types.InAssembly(typeof(Knight.Application.DependencyInjection).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Modules_ShouldNotDependOn_Infrastructure()
    {
        var moduleAssemblies = new[]
        {
            typeof(Tenancy.Domain.Tenant).Assembly,
            typeof(Identity.Domain.TenantUser).Assembly,
            typeof(FeatureManagement.Domain.TenantFeature).Assembly,
            typeof(Catalog.Domain.Category).Assembly,
            typeof(Ordering.Domain.Order).Assembly,
            typeof(Customer.Domain.Customer).Assembly,
            typeof(Fulfillment.Domain.TenantFulfillmentSettings).Assembly,
            typeof(Delivery.Domain.DeliveryZone).Assembly,
            typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly,
            typeof(Payment.Domain.Payment).Assembly,
            typeof(Promotions.Domain.Promotion).Assembly
        };

        foreach (var assembly in moduleAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn(InfrastructureNamespace)
                .GetResult();

            Assert.True(result.IsSuccessful, Describe(result));
        }
    }

    [Fact]
    public void Modules_ShouldNotDependOn_Api()
    {
        var moduleAssemblies = new[]
        {
            typeof(Tenancy.Domain.Tenant).Assembly,
            typeof(Identity.Domain.TenantUser).Assembly,
            typeof(FeatureManagement.Domain.TenantFeature).Assembly,
            typeof(Catalog.Domain.Category).Assembly,
            typeof(Ordering.Domain.Order).Assembly,
            typeof(Customer.Domain.Customer).Assembly,
            typeof(Fulfillment.Domain.TenantFulfillmentSettings).Assembly,
            typeof(Delivery.Domain.DeliveryZone).Assembly,
            typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly,
            typeof(Payment.Domain.Payment).Assembly,
            typeof(Promotions.Domain.Promotion).Assembly
        };

        foreach (var assembly in moduleAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn(ApiNamespace)
                .GetResult();

            Assert.True(result.IsSuccessful, Describe(result));
        }
    }

    [Fact]
    public void Ordering_ShouldNotDependOn_CatalogDomain()
    {
        var result = Types.InAssembly(typeof(Ordering.Domain.Order).Assembly)
            .Should()
            .NotHaveDependencyOn("Catalog.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Ordering_ShouldNotDependOn_CustomerDomain()
    {
        var result = Types.InAssembly(typeof(Ordering.Domain.Order).Assembly)
            .Should()
            .NotHaveDependencyOn("Customer.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Ordering_ShouldNotDependOn_DeliveryDomain()
    {
        var result = Types.InAssembly(typeof(Ordering.Domain.Order).Assembly)
            .Should()
            .NotHaveDependencyOn("Delivery.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Ordering_ShouldNotDependOn_FulfillmentDomain()
    {
        var result = Types.InAssembly(typeof(Ordering.Domain.Order).Assembly)
            .Should()
            .NotHaveDependencyOn("Fulfillment.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Customer_ShouldNotDependOn_Ordering()
    {
        var result = Types.InAssembly(typeof(Customer.Domain.Customer).Assembly)
            .Should()
            .NotHaveDependencyOn("Ordering")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Catalog_ShouldNotDependOn_Ordering()
    {
        var result = Types.InAssembly(typeof(Catalog.Domain.Category).Assembly)
            .Should()
            .NotHaveDependencyOn("Ordering")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Delivery_ShouldNotDependOn_Ordering()
    {
        var result = Types.InAssembly(typeof(Delivery.Domain.DeliveryZone).Assembly)
            .Should()
            .NotHaveDependencyOn("Ordering")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Fulfillment_ShouldNotDependOn_Ordering()
    {
        var result = Types.InAssembly(typeof(Fulfillment.Domain.TenantFulfillmentSettings).Assembly)
            .Should()
            .NotHaveDependencyOn("Ordering")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Checkout_ShouldNotDependOn_CatalogDomain()
    {
        var result = Types.InAssembly(typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly)
            .Should()
            .NotHaveDependencyOn("Catalog.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Checkout_ShouldNotDependOn_DeliveryDomain()
    {
        var result = Types.InAssembly(typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly)
            .Should()
            .NotHaveDependencyOn("Delivery.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Checkout_ShouldNotDependOn_CustomerDomain()
    {
        var result = Types.InAssembly(typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly)
            .Should()
            .NotHaveDependencyOn("Customer.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Checkout_ShouldNotDependOn_OrderingDomain()
    {
        var result = Types.InAssembly(typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly)
            .Should()
            .NotHaveDependencyOn("Ordering.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Checkout_ShouldNotDependOn_PaymentDomain()
    {
        var result = Types.InAssembly(typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly)
            .Should()
            .NotHaveDependencyOn("Payment.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Payment_ShouldNotDependOn_OrderingDomain()
    {
        var result = Types.InAssembly(typeof(Payment.Domain.Payment).Assembly)
            .Should()
            .NotHaveDependencyOn("Ordering.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Ordering_ShouldNotDependOn_PaymentDomain()
    {
        var result = Types.InAssembly(typeof(Ordering.Domain.Order).Assembly)
            .Should()
            .NotHaveDependencyOn("Payment.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Promotions_ShouldNotDependOn_OrderingDomain()
    {
        var result = Types.InAssembly(typeof(Promotions.Domain.Promotion).Assembly)
            .Should()
            .NotHaveDependencyOn("Ordering.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Promotions_ShouldNotDependOn_CheckoutDomain()
    {
        var result = Types.InAssembly(typeof(Promotions.Domain.Promotion).Assembly)
            .Should()
            .NotHaveDependencyOn("Checkout.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Promotions_ShouldNotDependOn_PaymentDomain()
    {
        var result = Types.InAssembly(typeof(Promotions.Domain.Promotion).Assembly)
            .Should()
            .NotHaveDependencyOn("Payment.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>
    /// Promotions evaluates against a subtotal the Ordering pricing path has already
    /// computed from Catalog; it must never reach into Catalog itself to reprice.
    /// </summary>
    [Fact]
    public void Promotions_ShouldNotDependOn_CatalogDomain()
    {
        var result = Types.InAssembly(typeof(Promotions.Domain.Promotion).Assembly)
            .Should()
            .NotHaveDependencyOn("Catalog.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Ordering_ShouldNotDependOn_PromotionsDomain()
    {
        var result = Types.InAssembly(typeof(Ordering.Domain.Order).Assembly)
            .Should()
            .NotHaveDependencyOn("Promotions.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Payment_ShouldNotDependOn_PromotionsDomain()
    {
        var result = Types.InAssembly(typeof(Payment.Domain.Payment).Assembly)
            .Should()
            .NotHaveDependencyOn("Promotions.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Checkout_ShouldNotDependOn_DeferredConcepts()
    {
        var result = Types.InAssembly(typeof(Checkout.Domain.CheckoutIdempotencyRecord).Assembly)
            .Should()
            .NotHaveDependencyOn("Cart")
            .And()
            .NotHaveDependencyOn("Loyalty")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Api_ShouldNotDependOn_Persistence()
    {
        // Endpoints must go through application services/repositories, never
        // touch PlatformDbContext or EF directly
        var result = Types.InAssembly(typeof(Program).Assembly)
            .Should()
            .NotHaveDependencyOn("Knight.Infrastructure.Persistence")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void DomainEntities_ShouldBeSealedOrAbstract()
    {
        var result = Types.InAssembly(typeof(Knight.Domain.Common.Entity).Assembly)
            .That()
            .Inherit(typeof(Knight.Domain.Common.Entity))
            .Should()
            .BeSealed()
            .Or()
            .BeAbstract()
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.FailingTypes is null
            ? "No details available."
            : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
