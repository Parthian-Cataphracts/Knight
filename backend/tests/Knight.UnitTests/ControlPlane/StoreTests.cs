using Knight.Domain.Exceptions;
using Stores.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class StoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();

    private static Store CreateStore(
        string domain = "cafe1.ir",
        StoreEnvironment environment = StoreEnvironment.Production,
        HostingModel hosting = HostingModel.SharedManaged) =>
        Store.Create(Guid.NewGuid(), Now, CustomerId, "Main store", "cafe1", domain, environment, hosting);

    [Fact]
    public void Create_StartsProvisioningAndUnregistered()
    {
        var store = CreateStore();

        Assert.Equal(StoreStatus.Provisioning, store.Status);
        Assert.Equal(IntegrationStatus.NotRegistered, store.IntegrationStatus);
        Assert.Null(store.ApplicationVersion);
        Assert.Null(store.LastSeenAt);
    }

    [Fact]
    public void Create_WithoutCustomer_Throws()
    {
        var exception = Assert.Throws<DomainException>(() =>
            Store.Create(Guid.NewGuid(), Now, Guid.Empty, "Main", "cafe1", "cafe1.ir", StoreEnvironment.Production, HostingModel.SharedManaged));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Theory]
    [InlineData("HTTPS://Cafe1.IR/menu", "cafe1.ir")]
    [InlineData("  cafe1.ir:8443  ", "cafe1.ir")]
    [InlineData("cafe1.ir.", "cafe1.ir")]
    [InlineData("کافه.ایران", "xn--mgb6csa30b.xn--mgba3a4f16a")]
    public void Create_NormalizesTheDomain(string input, string expected)
    {
        var store = CreateStore(input);

        Assert.Equal(expected, store.PrimaryDomain);
    }

    [Theory]
    [InlineData("not a host")]
    [InlineData("localhost")]
    [InlineData("-cafe1.ir")]
    public void Create_WithInvalidDomain_Throws(string input)
    {
        Assert.Throws<DomainException>(() => CreateStore(input));
    }

    [Fact]
    public void Activate_FromProvisioning_Succeeds()
    {
        var store = CreateStore();

        store.Activate(Now);

        Assert.Equal(StoreStatus.Active, store.Status);
    }

    [Fact]
    public void Suspend_FromProvisioning_IsRejected()
    {
        var store = CreateStore();

        var exception = Assert.Throws<DomainException>(() => store.Suspend(Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void Archive_IsTerminalAndRevokesCredentials()
    {
        var store = CreateStore();
        store.Activate(Now);
        var credential = store.IssueCredential(Guid.NewGuid(), "kn_cafe1_1", "hash", Now);

        store.Archive(Now);

        Assert.Equal(StoreStatus.Archived, store.Status);
        Assert.Equal(IntegrationStatus.Disconnected, store.IntegrationStatus);
        Assert.False(credential.IsUsable(Now));
        Assert.Throws<DomainException>(() => store.Activate(Now));
    }

    [Fact]
    public void MarkConnected_WithMatchingEnvironment_RecordsVersionAndContact()
    {
        var store = CreateStore();

        store.MarkConnected(StoreEnvironment.Production, "4.2.0", Now);

        Assert.Equal(IntegrationStatus.Connected, store.IntegrationStatus);
        Assert.Equal("4.2.0", store.ApplicationVersion);
        Assert.Equal(Now, store.LastSeenAt);
    }

    [Fact]
    public void MarkConnected_WithMismatchedEnvironment_IsRejected()
    {
        var store = CreateStore(environment: StoreEnvironment.Production);

        var exception = Assert.Throws<DomainException>(() =>
            store.MarkConnected(StoreEnvironment.Staging, "4.2.0", Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
        Assert.Equal(IntegrationStatus.NotRegistered, store.IntegrationStatus);
    }

    [Fact]
    public void RotateCredential_KeepsThePreviousOneUsableForTheGraceWindow()
    {
        var store = CreateStore();
        var current = store.IssueCredential(Guid.NewGuid(), "kn_cafe1_1", "hash-1", Now);

        var replacement = store.RotateCredential(
            current.Id,
            Guid.NewGuid(),
            "kn_cafe1_2",
            "hash-2",
            TimeSpan.FromHours(24),
            Now);

        Assert.True(current.IsUsable(Now.AddHours(23)));
        Assert.False(current.IsUsable(Now.AddHours(25)));
        Assert.True(replacement.IsUsable(Now.AddHours(25)));
        Assert.Equal(StoreCredentialState.GracePeriod, current.StateAt(Now.AddHours(1)));
    }

    [Fact]
    public void RotateCredential_AfterRevocation_IsRejected()
    {
        var store = CreateStore();
        var credential = store.IssueCredential(Guid.NewGuid(), "kn_cafe1_1", "hash-1", Now);
        store.RevokeCredential(credential.Id, Now);

        Assert.Throws<DomainException>(() =>
            store.RotateCredential(credential.Id, Guid.NewGuid(), "kn_cafe1_2", "hash-2", TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void RevokedCredential_CannotRecordUse()
    {
        var store = CreateStore();
        var credential = store.IssueCredential(Guid.NewGuid(), "kn_cafe1_1", "hash", Now);
        store.RevokeCredential(credential.Id, Now);

        Assert.Throws<DomainException>(() => credential.RecordUse(Now));
        Assert.Equal(StoreCredentialState.Revoked, credential.StateAt(Now));
    }

    [Fact]
    public void IssueCredential_OnArchivedStore_IsRejected()
    {
        var store = CreateStore();
        store.Archive(Now);

        Assert.Throws<DomainException>(() => store.IssueCredential(Guid.NewGuid(), "kn_cafe1_1", "hash", Now));
    }

    [Fact]
    public void IssueCredential_WithoutSecretHash_IsRejected()
    {
        var store = CreateStore();

        var exception = Assert.Throws<DomainException>(() =>
            store.IssueCredential(Guid.NewGuid(), "kn_cafe1_1", "   ", Now));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }
}
