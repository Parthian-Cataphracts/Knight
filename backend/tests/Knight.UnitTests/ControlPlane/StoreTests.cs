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
    public void CompleteHandshake_WithMatchingEnvironment_RecordsVersionAndContact()
    {
        var store = CreateStore();

        var outcome = store.CompleteHandshake(StoreEnvironment.Production, "4.2.0", requireDomainVerification: false, Now);

        Assert.Equal(IntegrationStatus.Connected, store.IntegrationStatus);
        Assert.Equal("4.2.0", store.ApplicationVersion);
        Assert.Equal(Now, store.LastSeenAt);
        Assert.True(outcome.VersionChanged);
        Assert.Null(outcome.PreviousVersion);
    }

    [Fact]
    public void CompleteHandshake_WithMismatchedEnvironment_IsRejected()
    {
        var store = CreateStore(environment: StoreEnvironment.Production);

        var exception = Assert.Throws<DomainException>(() =>
            store.CompleteHandshake(StoreEnvironment.Staging, "4.2.0", requireDomainVerification: false, Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
        Assert.Equal(IntegrationStatus.NotRegistered, store.IntegrationStatus);
    }

    /// <summary>
    /// Credentials prove possession of a secret; they say nothing about who
    /// answers on the domain KNIGHT will call back on. Until that is proven the
    /// link is Pending, not Connected.
    /// </summary>
    [Fact]
    public void CompleteHandshake_WithoutDomainVerification_StaysPending()
    {
        var store = CreateStore();

        var outcome = store.CompleteHandshake(StoreEnvironment.Production, "4.2.0", requireDomainVerification: true, Now);

        Assert.Equal(IntegrationStatus.Pending, store.IntegrationStatus);
        Assert.True(outcome.DomainVerificationOutstanding);
        Assert.Equal(Now, store.LastSeenAt);
    }

    [Fact]
    public void CompleteHandshake_AfterDomainVerification_Connects()
    {
        var store = CreateStore();
        store.IssueDomainVerification("knight-verify-abc", Now);
        store.MarkDomainVerified(DomainVerificationMethod.HttpToken, Now);

        var outcome = store.CompleteHandshake(StoreEnvironment.Production, "4.2.0", requireDomainVerification: true, Now);

        Assert.Equal(IntegrationStatus.Connected, store.IntegrationStatus);
        Assert.False(outcome.DomainVerificationOutstanding);
    }

    [Fact]
    public void MarkDomainVerified_WithoutAnIssuedToken_IsRejected()
    {
        var store = CreateStore();

        var exception = Assert.Throws<DomainException>(() =>
            store.MarkDomainVerified(DomainVerificationMethod.HttpToken, Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    /// <summary>
    /// Proof is about one domain. Moving the store to another must not carry the
    /// old proof across, or a store could verify a domain it controls and then
    /// point KNIGHT at one it does not.
    /// </summary>
    [Fact]
    public void UpdateProfile_WithANewDomain_DropsTheProofOfTheOldOne()
    {
        var store = CreateStore();
        store.IssueDomainVerification("knight-verify-abc", Now);
        store.MarkDomainVerified(DomainVerificationMethod.HttpToken, Now);

        store.UpdateProfile(store.Name, "elsewhere.example.test", Now);

        Assert.False(store.IsDomainVerified);
        Assert.Null(store.DomainVerificationToken);
    }

    [Fact]
    public void UpdateProfile_WithTheSameDomain_KeepsTheProof()
    {
        var store = CreateStore();
        store.IssueDomainVerification("knight-verify-abc", Now);
        store.MarkDomainVerified(DomainVerificationMethod.HttpToken, Now);

        store.UpdateProfile("Renamed", store.PrimaryDomain, Now);

        Assert.True(store.IsDomainVerified);
    }

    [Theory]
    [InlineData(StoreHealthStatus.Healthy, IntegrationStatus.Connected)]
    [InlineData(StoreHealthStatus.Degraded, IntegrationStatus.Degraded)]
    [InlineData(StoreHealthStatus.Unhealthy, IntegrationStatus.Degraded)]
    [InlineData(StoreHealthStatus.Unreachable, IntegrationStatus.Disconnected)]
    public void RecordObservation_MapsWhatWasSeenToTheLinkState(StoreHealthStatus observed, IntegrationStatus expected)
    {
        var store = CreateStore();

        store.RecordObservation(observed, "4.2.0", requireDomainVerification: false, Now);

        Assert.Equal(expected, store.IntegrationStatus);
    }

    /// <summary>
    /// A poll that never got an answer is not contact. Advancing LastSeenAt on it
    /// would make a store that has been down for a week look freshly seen.
    /// </summary>
    [Fact]
    public void RecordObservation_WhenUnreachable_DoesNotAdvanceLastSeen()
    {
        var store = CreateStore();
        store.CompleteHandshake(StoreEnvironment.Production, "4.2.0", requireDomainVerification: false, Now);

        store.RecordObservation(StoreHealthStatus.Unreachable, null, requireDomainVerification: false, Now.AddHours(1));

        Assert.Equal(Now, store.LastSeenAt);
        Assert.Equal(IntegrationStatus.Disconnected, store.IntegrationStatus);
    }

    [Fact]
    public void RecordObservation_WithAnUnchangedVersion_ReportsNoDeployment()
    {
        var store = CreateStore();
        store.CompleteHandshake(StoreEnvironment.Production, "4.2.0", requireDomainVerification: false, Now);

        var outcome = store.RecordObservation(StoreHealthStatus.Healthy, "4.2.0", requireDomainVerification: false, Now);

        Assert.False(outcome.VersionChanged);
    }

    [Fact]
    public void RecordObservation_WithANewVersion_ReportsThePreviousOne()
    {
        var store = CreateStore();
        store.CompleteHandshake(StoreEnvironment.Production, "4.2.0", requireDomainVerification: false, Now);

        var outcome = store.RecordObservation(StoreHealthStatus.Healthy, "4.3.0", requireDomainVerification: false, Now);

        Assert.True(outcome.VersionChanged);
        Assert.Equal("4.2.0", outcome.PreviousVersion);
        Assert.Equal("4.3.0", store.ApplicationVersion);
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
