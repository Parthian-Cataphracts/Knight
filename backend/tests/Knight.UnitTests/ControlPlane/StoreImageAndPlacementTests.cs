using FeatureRegistry.Domain;
using Knight.Domain.Exceptions;
using Servers.Domain;
using Stores.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The base store image, the dedication of a machine to one customer, and the
/// mutual-TLS binding a store may carry.
///
/// All three are phase 9's answer to "professional infrastructure", and each of
/// them is a promise somebody pays for: the image is signed code, a dedicated
/// machine is exclusivity, and mutual TLS is a second factor on the transport.
/// A promise that can be set to a meaningless value is not one.
/// </summary>
public sealed class StoreImageAndPlacementTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
    private const string Digest = "3b1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f7081920a1b2c3d4";
    private const string Thumbprint = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private static StoreImage Image(string version = "2.3.0", string storeVersion = "2.3.0") =>
        StoreImage.Create(
            Guid.CreateVersion7(),
            Now,
            version,
            storeVersion,
            "images/store-2.3.0.zip",
            Digest,
            42_000_000,
            "c2lnbmF0dXJl",
            "dev",
            null);

    // --- Base store image --------------------------------------------------

    [Fact]
    public void ANewImage_IsADraftUntilItIsPublished()
    {
        var image = Image();

        Assert.Equal(StoreImageStatus.Draft, image.Status);
        Assert.False(image.IsUsable);

        image.Publish(Guid.CreateVersion7(), Now);

        Assert.True(image.IsUsable);
        Assert.Equal(Now, image.PublishedAt);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("2026-08-20-build")]
    public void AnImageVersionThatIsNotSemantic_IsRefused(string version)
    {
        Assert.Throws<DomainException>(() => Image(version));
    }

    [Fact]
    public void AStoreVersionThatIsNotSemantic_IsRefused()
    {
        // Feature compatibility ranges are resolved against it, so an
        // unparseable store version would make every range check on that store
        // silently unanswerable.
        Assert.Throws<DomainException>(() => Image(storeVersion: "nightly"));
    }

    [Fact]
    public void AYankedImage_StaysReadableAndStopsBeingUsable()
    {
        var image = Image();
        image.Publish(Guid.CreateVersion7(), Now);

        image.Yank("The bundled nginx config allowed directory listing.", Now);

        Assert.Equal(StoreImageStatus.Yanked, image.Status);
        Assert.False(image.IsUsable);
        Assert.Equal("2.3.0", image.Version);
    }

    [Fact]
    public void AYankMustSayWhy()
    {
        var image = Image();
        image.Publish(Guid.CreateVersion7(), Now);

        Assert.Throws<DomainException>(() => image.Yank("  ", Now));
    }

    // --- Dedicated machines ------------------------------------------------

    [Fact]
    public void ADedicatedServer_RecordsTheCustomerItBelongsTo()
    {
        var customer = Guid.CreateVersion7();
        var server = Server.Register(
            Guid.CreateVersion7(),
            Now,
            "dedicated-01",
            ServerHostingModel.DedicatedManaged,
            ServerEnvironment.Production);

        server.DedicateTo(customer, Now);

        Assert.Equal(customer, server.DedicatedCustomerId);
    }

    [Fact]
    public void ADedicatedServer_CannotBeDedicatedToNobody()
    {
        var server = Server.Register(
            Guid.CreateVersion7(),
            Now,
            "dedicated-01",
            ServerHostingModel.DedicatedManaged,
            ServerEnvironment.Production);

        Assert.Throws<DomainException>(() => server.DedicateTo(null, Now));
    }

    [Fact]
    public void ASharedServer_CannotBeDedicatedToOneCustomer()
    {
        var server = Server.Register(
            Guid.CreateVersion7(),
            Now,
            "shared-01",
            ServerHostingModel.SharedManaged,
            ServerEnvironment.Production);

        Assert.Throws<DomainException>(() => server.DedicateTo(Guid.CreateVersion7(), Now));
    }

    // --- Mutual TLS --------------------------------------------------------

    private static Store StoreOn(HostingModel hosting) =>
        Store.Create(
            Guid.CreateVersion7(),
            Now,
            Guid.CreateVersion7(),
            "Acme",
            "acme",
            "shop.acme.test",
            StoreEnvironment.Production,
            hosting);

    [Fact]
    public void ADedicatedStore_CanBeBoundToAClientCertificate()
    {
        var store = StoreOn(HostingModel.DedicatedManaged);

        store.RequireMutualTls(Thumbprint.ToUpperInvariant(), Now);

        Assert.True(store.RequiresMutualTls);
        Assert.Equal(Thumbprint, store.MutualTlsThumbprint);
    }

    [Fact]
    public void ASharedStore_CannotBeBoundToAClientCertificate()
    {
        var store = StoreOn(HostingModel.SharedManaged);

        Assert.Throws<DomainException>(() => store.RequireMutualTls(Thumbprint, Now));
    }

    [Theory]
    [InlineData("a1b2c3")]
    [InlineData("zzzz2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    public void AThumbprintThatIsNotASha256_IsRefused(string thumbprint)
    {
        var store = StoreOn(HostingModel.DedicatedManaged);

        // A binding nobody can satisfy locks the store out of its own control
        // plane, so a malformed thumbprint is refused rather than stored.
        Assert.Throws<DomainException>(() => store.RequireMutualTls(thumbprint, Now));
    }

    [Fact]
    public void ClearingTheBinding_LeavesTheCredentialAloneAsTheOnlyFactor()
    {
        var store = StoreOn(HostingModel.CustomerManaged);
        store.RequireMutualTls(Thumbprint, Now);

        store.ClearMutualTls(Now);

        Assert.False(store.RequiresMutualTls);
        Assert.Null(store.MutualTlsThumbprint);
    }
}
