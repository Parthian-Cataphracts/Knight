using Stores.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The credential checks a store must pass before it may ingest anything
/// (docs/authentication.md section 2).
/// </summary>
public sealed class StoreHandshakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ClientId = "knight-cafe1-abc123def456";
    private const string SecretHash = "hashed-secret";

    private static Store CreateStore(StoreEnvironment environment = StoreEnvironment.Production)
    {
        var store = Store.Create(
            Guid.NewGuid(),
            Now,
            Guid.NewGuid(),
            "Main store",
            "cafe1",
            "cafe1.ir",
            environment,
            HostingModel.SharedManaged);

        store.Activate(Now);
        store.IssueCredential(Guid.NewGuid(), ClientId, SecretHash, Now);
        return store;
    }

    private static bool Matches(string hash) => hash == SecretHash;

    private static bool NeverMatches(string hash) => false;

    [Fact]
    public void AValidCredentialIsAccepted()
    {
        var result = StoreHandshake.Verify(CreateStore(), ClientId, Matches, StoreEnvironment.Production, Now);

        Assert.True(result.IsAccepted);
        Assert.NotNull(result.Credential);
    }

    [Fact]
    public void AnUnknownClientIdIsRefused()
    {
        var result = StoreHandshake.Verify(CreateStore(), "knight-other-000000000000", Matches, StoreEnvironment.Production, Now);

        Assert.Equal(HandshakeRefusal.UnknownCredential, result.Refusal);
    }

    [Fact]
    public void AWrongSecretIsRefusedTheSameWayAsAnUnknownClient()
    {
        var result = StoreHandshake.Verify(CreateStore(), ClientId, NeverMatches, StoreEnvironment.Production, Now);

        // Deliberately indistinguishable: telling a caller which half of the
        // credential was wrong tells an attacker which half to keep working on.
        Assert.Equal(HandshakeRefusal.UnknownCredential, result.Refusal);
    }

    [Fact]
    public void ARevokedCredentialIsRefused()
    {
        var store = CreateStore();
        store.RevokeCredential(store.Credentials.Single().Id, Now);

        var result = StoreHandshake.Verify(store, ClientId, Matches, StoreEnvironment.Production, Now);

        Assert.Equal(HandshakeRefusal.CredentialNotUsable, result.Refusal);
    }

    [Fact]
    public void ACredentialInsideItsGraceWindowStillWorks()
    {
        var store = CreateStore();
        var current = store.Credentials.Single();
        store.RotateCredential(current.Id, Guid.NewGuid(), "knight-cafe1-new000000000", "new-hash", TimeSpan.FromHours(24), Now);

        // Rotation must not cut a running store off before it has picked the new
        // secret up (risks.md R8).
        var result = StoreHandshake.Verify(store, ClientId, Matches, StoreEnvironment.Production, Now.AddHours(1));

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void ACredentialPastItsGraceWindowIsRefused()
    {
        var store = CreateStore();
        var current = store.Credentials.Single();
        store.RotateCredential(current.Id, Guid.NewGuid(), "knight-cafe1-new000000000", "new-hash", TimeSpan.FromHours(24), Now);

        var result = StoreHandshake.Verify(store, ClientId, Matches, StoreEnvironment.Production, Now.AddHours(25));

        Assert.Equal(HandshakeRefusal.CredentialNotUsable, result.Refusal);
    }

    [Fact]
    public void ASuspendedStoreCannotHandshake()
    {
        var store = CreateStore();
        store.Suspend(Now);

        var result = StoreHandshake.Verify(store, ClientId, Matches, StoreEnvironment.Production, Now);

        Assert.Equal(HandshakeRefusal.StoreNotOperable, result.Refusal);
    }

    [Fact]
    public void AnArchivedStoreCannotHandshake()
    {
        var store = CreateStore();
        store.Archive(Now);

        // Archiving revokes the credentials too, so either refusal is correct;
        // what matters is that it is refused.
        var result = StoreHandshake.Verify(store, ClientId, Matches, StoreEnvironment.Production, Now);

        Assert.False(result.IsAccepted);
    }

    [Fact]
    public void AStoreReportingTheWrongEnvironmentIsRefused()
    {
        var result = StoreHandshake.Verify(CreateStore(), ClientId, Matches, StoreEnvironment.Staging, Now);

        Assert.Equal(HandshakeRefusal.EnvironmentMismatch, result.Refusal);
    }

    [Fact]
    public void TheEnvironmentIsCheckedAfterTheCredential()
    {
        // A wrong environment with a wrong secret must still read as an unknown
        // credential: otherwise the response distinguishes a real client id from
        // an invented one.
        var result = StoreHandshake.Verify(CreateStore(), ClientId, NeverMatches, StoreEnvironment.Staging, Now);

        Assert.Equal(HandshakeRefusal.UnknownCredential, result.Refusal);
    }
}
