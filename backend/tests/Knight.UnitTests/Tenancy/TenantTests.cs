using Knight.Domain.Exceptions;
using Tenancy.Domain;
using Xunit;

namespace Knight.UnitTests.Tenancy;

public sealed class TenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Tenant CreateTenant(string slug = "acme-co") =>
        Tenant.Create(Guid.NewGuid(), Now, "Acme Co", slug, "UTC", "USD");

    [Fact]
    public void Create_WithValidData_StartsInPendingStatus()
    {
        var tenant = CreateTenant();

        Assert.Equal(TenantStatus.Pending, tenant.Status);
        Assert.Equal("acme-co", tenant.Slug);
    }

    [Fact]
    public void Create_WithEmptyName_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => Tenant.Create(Guid.NewGuid(), Now, "  ", "slug", "UTC", "USD"));
    }

    [Theory]
    [InlineData("Acme Co")]
    [InlineData("acme_co")]
    [InlineData("-acme-co")]
    [InlineData("acme-co-")]
    [InlineData("acme--co")]
    [InlineData("")]
    public void Create_WithInvalidSlug_ThrowsDomainException(string slug)
    {
        Assert.Throws<DomainException>(() => Tenant.Create(Guid.NewGuid(), Now, "Acme Co", slug, "UTC", "USD"));
    }

    [Fact]
    public void Create_NormalizesSlugCasingAndWhitespace()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), Now, "Acme Co", "  Acme-Co  ", "UTC", "USD");

        Assert.Equal("acme-co", tenant.Slug);
    }

    [Fact]
    public void Create_WithInvalidTimeZone_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => Tenant.Create(Guid.NewGuid(), Now, "Acme Co", "acme-co", "Not/AZone", "USD"));
    }

    [Theory]
    [InlineData("US")]
    [InlineData("usdd")]
    [InlineData("123")]
    public void Create_WithInvalidCurrency_ThrowsDomainException(string currency)
    {
        Assert.Throws<DomainException>(() => Tenant.Create(Guid.NewGuid(), Now, "Acme Co", "acme-co", "UTC", currency));
    }

    [Fact]
    public void Create_NormalizesCurrencyToUppercase()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), Now, "Acme Co", "acme-co", "UTC", "usd");

        Assert.Equal("USD", tenant.DefaultCurrency);
    }

    // --- Lifecycle -----------------------------------------------------

    [Fact]
    public void Activate_FromPending_TransitionsToActive()
    {
        var tenant = CreateTenant();

        tenant.Activate(Now.AddMinutes(1));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.NotNull(tenant.UpdatedAt);
    }

    [Fact]
    public void Activate_FromSuspended_TransitionsToActive()
    {
        var tenant = CreateTenant();
        tenant.Activate(Now.AddMinutes(1));
        tenant.Suspend(Now.AddMinutes(2));

        tenant.Activate(Now.AddMinutes(3));

        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Fact]
    public void Activate_WhenArchived_ThrowsDomainException()
    {
        var tenant = CreateTenant();
        tenant.Activate(Now.AddMinutes(1));
        tenant.Archive(Now.AddMinutes(2));

        Assert.Throws<DomainException>(() => tenant.Activate(Now.AddMinutes(3)));
    }

    [Fact]
    public void Suspend_WhenPending_ThrowsDomainException()
    {
        var tenant = CreateTenant();

        Assert.Throws<DomainException>(() => tenant.Suspend(Now.AddMinutes(1)));
    }

    [Fact]
    public void Suspend_WhenActive_TransitionsToSuspended()
    {
        var tenant = CreateTenant();
        tenant.Activate(Now.AddMinutes(1));

        tenant.Suspend(Now.AddMinutes(2));

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
    }

    [Fact]
    public void Archive_WhenPending_ThrowsDomainException()
    {
        var tenant = CreateTenant();

        Assert.Throws<DomainException>(() => tenant.Archive(Now.AddMinutes(1)));
    }

    [Fact]
    public void Archive_FromActive_TransitionsToArchived()
    {
        var tenant = CreateTenant();
        tenant.Activate(Now.AddMinutes(1));

        tenant.Archive(Now.AddMinutes(2));

        Assert.Equal(TenantStatus.Archived, tenant.Status);
    }

    [Fact]
    public void Archive_FromSuspended_TransitionsToArchived()
    {
        var tenant = CreateTenant();
        tenant.Activate(Now.AddMinutes(1));
        tenant.Suspend(Now.AddMinutes(2));

        tenant.Archive(Now.AddMinutes(3));

        Assert.Equal(TenantStatus.Archived, tenant.Status);
    }

    // --- Domains ---------------------------------------------------------

    [Fact]
    public void AddDomain_NormalizesHost()
    {
        var tenant = CreateTenant();

        var domain = tenant.AddDomain(Guid.NewGuid(), "  ACME-CO.Example.COM.  ", TenantDomainType.Primary, makePrimary: false, Now);

        Assert.Equal("acme-co.example.com", domain.Host);
    }

    [Theory]
    [InlineData("https://acme.example.com")]
    [InlineData("acme.example.com/path")]
    [InlineData("acme.example.com:8080")]
    [InlineData("not a host")]
    public void AddDomain_WithInvalidHost_ThrowsDomainException(string host)
    {
        var tenant = CreateTenant();

        Assert.Throws<DomainException>(() => tenant.AddDomain(Guid.NewGuid(), host, TenantDomainType.Primary, makePrimary: false, Now));
    }

    [Fact]
    public void AddDomain_WithDuplicateHostOnSameTenant_ThrowsDomainException()
    {
        var tenant = CreateTenant();
        tenant.AddDomain(Guid.NewGuid(), "acme.example.com", TenantDomainType.Primary, makePrimary: false, Now);

        Assert.Throws<DomainException>(() =>
            tenant.AddDomain(Guid.NewGuid(), "ACME.example.com", TenantDomainType.Alias, makePrimary: false, Now));
    }

    [Fact]
    public void AddDomain_ToArchivedTenant_ThrowsDomainException()
    {
        var tenant = CreateTenant();
        tenant.Activate(Now.AddMinutes(1));
        tenant.Archive(Now.AddMinutes(2));

        Assert.Throws<DomainException>(() =>
            tenant.AddDomain(Guid.NewGuid(), "acme.example.com", TenantDomainType.Primary, makePrimary: false, Now.AddMinutes(3)));
    }

    [Fact]
    public void SetPrimaryDomain_DemotesPreviousPrimaryOfSameType()
    {
        var tenant = CreateTenant();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        tenant.AddDomain(firstId, "old.example.com", TenantDomainType.Primary, makePrimary: true, Now);
        tenant.AddDomain(secondId, "new.example.com", TenantDomainType.Primary, makePrimary: false, Now);

        tenant.SetPrimaryDomain(secondId, Now.AddMinutes(1));

        var oldDomain = tenant.Domains.Single(d => d.Id == firstId);
        var newDomain = tenant.Domains.Single(d => d.Id == secondId);

        Assert.False(oldDomain.IsPrimary);
        Assert.True(newDomain.IsPrimary);
        Assert.Equal(newDomain, tenant.GetPrimaryDomain());
    }

    [Fact]
    public void SetPrimaryDomain_DoesNotAffectPrimaryOfDifferentType()
    {
        var tenant = CreateTenant();
        var primaryId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        tenant.AddDomain(primaryId, "storefront.example.com", TenantDomainType.Primary, makePrimary: true, Now);
        tenant.AddDomain(adminId, "admin.example.com", TenantDomainType.Admin, makePrimary: true, Now);

        Assert.True(tenant.Domains.Single(d => d.Id == primaryId).IsPrimary);
        Assert.True(tenant.Domains.Single(d => d.Id == adminId).IsPrimary);
    }

    [Fact]
    public void RemoveDomain_RemovesFromCollection()
    {
        var tenant = CreateTenant();
        var domainId = Guid.NewGuid();
        tenant.AddDomain(domainId, "acme.example.com", TenantDomainType.Primary, makePrimary: false, Now);

        tenant.RemoveDomain(domainId, Now.AddMinutes(1));

        Assert.Empty(tenant.Domains);
    }

    [Fact]
    public void RemoveDomain_WhenDomainNotOwnedByTenant_ThrowsDomainException()
    {
        var tenant = CreateTenant();

        Assert.Throws<DomainException>(() => tenant.RemoveDomain(Guid.NewGuid(), Now.AddMinutes(1)));
    }
}
