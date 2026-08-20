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
    public void Api_ShouldNotDependOn_EntityFrameworkCore()
    {
        // Endpoints go through module services and repositories. An endpoint
        // holding a DbContext is how query filters get bypassed, and customer
        // isolation is enforced by exactly one of those.
        var result = Types.InAssembly(typeof(Program).Assembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
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
