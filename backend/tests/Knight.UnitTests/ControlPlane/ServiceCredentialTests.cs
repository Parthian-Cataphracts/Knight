using System.Text.Json;
using FeatureDelivery;
using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Security;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Issuing, rotating and revoking the secret a store signs a Feature's service
/// with (<c>docs/adr/0034-a-shared-secret-has-a-lifetime.md</c>).
///
/// Three things are worth testing here and they are all about ordering and
/// blast radius rather than about arithmetic:
///
/// - the **service is told first**, so a store never holds a credential the
///   service has not heard of;
/// - a rotation **keeps the Feature's other secrets**, because the store holds
///   one sealed document and a payment key must survive a shared secret being
///   replaced;
/// - nothing anybody can read afterwards — an audit entry, a response —
///   contains the value.
/// </summary>
public sealed class ServiceCredentialTests
{
    private static readonly Guid StoreId = Guid.NewGuid();
    private static readonly Guid FeatureId = Guid.NewGuid();
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    private const string SecretName = "SUBSCRIPTIONS_SERVICE_SECRET";
    private const string Issued = "a-freshly-minted-shared-secret";

    private readonly IFeatureInstallationRepository _installations = Substitute.For<IFeatureInstallationRepository>();
    private readonly IFeatureConfigurationRepository _configurations = Substitute.For<IFeatureConfigurationRepository>();
    private readonly IFeatureDeliveryService _delivery = Substitute.For<IFeatureDeliveryService>();
    private readonly IServiceEndpointReader _endpoints = Substitute.For<IServiceEndpointReader>();
    private readonly IServiceControlPlane _controlPlane = Substitute.For<IServiceControlPlane>();
    private readonly ISecureTokenFactory _tokens = Substitute.For<ISecureTokenFactory>();
    private readonly ISecretProtector _secrets = Substitute.For<ISecretProtector>();
    private readonly IAuditTrail _audit = Substitute.For<IAuditTrail>();

    private readonly ServiceEndpointDescriptor _endpoint = new(
        StoreId,
        FeatureId,
        "subscriptions",
        "camden-coffee",
        new Uri("https://subscriptions.knight.dev"),
        SecretName);

    private readonly IServiceCredentialService _service;

    public ServiceCredentialTests()
    {
        _tokens.Generate().Returns(new GeneratedSecret(Issued, "hashed"));

        // The sealing is somebody else's tested concern; here it is the identity
        // function so the document under test stays readable.
        _secrets.Protect(Arg.Any<string>()).Returns(call => call.Arg<string>());
        _secrets.Unprotect(Arg.Any<string>()).Returns(call => call.Arg<string>());

        _endpoints
            .ForInstallationAsync(StoreId, FeatureId, Arg.Any<CancellationToken>())
            .Returns(_endpoint);

        _service = new ServiceCredentialService(
            _installations,
            _configurations,
            _delivery,
            _endpoints,
            _controlPlane,
            _tokens,
            _secrets,
            _audit,
            NullLogger<ServiceCredentialService>.Instance);
    }

    private static FeatureConfiguration Configuration(params (string Name, string Value)[] secrets)
    {
        var document = secrets.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

        return FeatureConfiguration.Create(
            Guid.NewGuid(),
            Now,
            StoreId,
            CustomerId,
            FeatureId,
            """{"retry_attempts":3}""",
            document.Count == 0 ? null : JsonSerializer.Serialize(document),
            JsonSerializer.Serialize(document.Keys.Order(StringComparer.Ordinal)),
            Guid.NewGuid());
    }

    private void Stored(FeatureConfiguration? configuration) =>
        _configurations
            .FindAsync(StoreId, FeatureId, Arg.Any<CancellationToken>())
            .Returns(configuration);

    private IReadOnlyDictionary<string, string> DeliveredSecrets() =>
        (IReadOnlyDictionary<string, string>)_delivery.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IFeatureDeliveryService.ConfigureAsync))
            .GetArguments()[3]!;

    // --- Issuing ------------------------------------------------------------

    [Fact]
    public async Task A_store_with_no_secret_is_registered_rather_than_rotated()
    {
        Stored(null);

        var result = await _service.IssueAsync(StoreId, FeatureId, null, CancellationToken.None);

        await _controlPlane.Received(1).RegisterAsync(_endpoint, Issued, Arg.Any<CancellationToken>());
        await _controlPlane.DidNotReceive().RotateAsync(
            Arg.Any<ServiceEndpointDescriptor>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // The distinction is worth keeping: the first one is a store being
        // connected and the second is a credential being replaced.
        Assert.False(result.Rotated);
        Assert.Equal(SecretName, result.SecretName);
    }

    [Fact]
    public async Task The_secret_reaches_the_store_as_a_configuration_secret()
    {
        Stored(null);

        await _service.IssueAsync(StoreId, FeatureId, null, CancellationToken.None);

        // Down the path every other secret already travels. Nothing new arrives
        // at the store and nothing here knows how a store applies one.
        Assert.Equal(Issued, DeliveredSecrets()[SecretName]);
    }

    [Fact]
    public async Task A_rotation_keeps_the_features_other_secrets()
    {
        Stored(Configuration((SecretName, "the-old-one"), ("PROVIDER_API_KEY", "a-payment-key")));

        await _service.IssueAsync(StoreId, FeatureId, 600, CancellationToken.None);

        var delivered = DeliveredSecrets();

        // The store holds one sealed document. A merchant's payment key must
        // survive a shared secret being replaced.
        Assert.Equal(Issued, delivered[SecretName]);
        Assert.Equal("a-payment-key", delivered["PROVIDER_API_KEY"]);
    }

    [Fact]
    public async Task A_rotation_tells_the_service_how_long_the_old_secret_lives()
    {
        Stored(Configuration((SecretName, "the-old-one")));

        var result = await _service.IssueAsync(StoreId, FeatureId, 600, CancellationToken.None);

        await _controlPlane.Received(1).RotateAsync(_endpoint, Issued, 600, Arg.Any<CancellationToken>());
        Assert.True(result.Rotated);
        Assert.Equal(600, result.OverlapSeconds);
    }

    [Fact]
    public async Task An_overlap_nobody_asked_for_is_the_default_and_a_silly_one_is_clamped()
    {
        Stored(Configuration((SecretName, "the-old-one")));

        await _service.IssueAsync(StoreId, FeatureId, null, CancellationToken.None);
        await _service.IssueAsync(StoreId, FeatureId, 10 * 24 * 3600, CancellationToken.None);

        // A week from a typo would leave a replaced secret valid for a week, and
        // this is the one number where a mistake is silent.
        await _controlPlane.Received(1).RotateAsync(
            _endpoint, Issued, ServiceCredentialService.DefaultOverlapSeconds, Arg.Any<CancellationToken>());
        await _controlPlane.Received(1).RotateAsync(
            _endpoint, Issued, ServiceCredentialService.MaximumOverlapSeconds, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_service_that_refuses_leaves_the_store_with_the_secret_it_had()
    {
        Stored(Configuration((SecretName, "the-old-one")));

        _controlPlane
            .RotateAsync(Arg.Any<ServiceEndpointDescriptor>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflictException("The service did not answer."));

        await Assert.ThrowsAsync<ConflictException>(
            () => _service.IssueAsync(StoreId, FeatureId, 600, CancellationToken.None));

        // The whole reason the service is told first. A store holding a
        // credential the service has not heard of is a store signing with
        // something that cannot verify, and that window is an outage.
        await _delivery.DidNotReceive().ConfigureAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_in_process_feature_has_no_shared_secret_to_issue()
    {
        _endpoints.ForInstallationAsync(StoreId, FeatureId, Arg.Any<CancellationToken>()).Returns((ServiceEndpointDescriptor?)null);

        // Issuing one would put a credential in a store's configuration that
        // nothing will ever check.
        await Assert.ThrowsAsync<ConflictException>(
            () => _service.IssueAsync(StoreId, FeatureId, null, CancellationToken.None));
    }

    [Fact]
    public async Task Nothing_written_to_the_audit_trail_carries_the_value()
    {
        Stored(null);

        await _service.IssueAsync(StoreId, FeatureId, null, CancellationToken.None);

        var recorded = _audit.ReceivedCalls().Single().GetArguments();

        Assert.DoesNotContain(Issued, JsonSerializer.Serialize(recorded[^1]));
    }

    // --- Revoking -----------------------------------------------------------

    [Fact]
    public async Task Revoking_tells_the_service_and_takes_the_secret_off_the_store()
    {
        Stored(Configuration((SecretName, "the-old-one"), ("PROVIDER_API_KEY", "a-payment-key")));

        await _service.RevokeAsync(StoreId, FeatureId, CancellationToken.None);

        await _controlPlane.Received(1).RevokeAsync(_endpoint, Arg.Any<CancellationToken>());

        var delivered = DeliveredSecrets();
        Assert.False(delivered.ContainsKey(SecretName));
        Assert.Equal("a-payment-key", delivered["PROVIDER_API_KEY"]);
    }

    [Fact]
    public async Task Revoking_a_store_that_never_had_a_secret_changes_no_configuration()
    {
        Stored(null);

        await _service.RevokeAsync(StoreId, FeatureId, CancellationToken.None);

        // Still told the service, because this is the half a store cannot be
        // trusted with and the service may know about a registration the
        // configuration does not.
        await _controlPlane.Received(1).RevokeAsync(_endpoint, Arg.Any<CancellationToken>());
        await _delivery.DidNotReceive().ConfigureAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_withdrawn_entitlement_revokes_every_store_that_runs_it_as_a_service()
    {
        var otherStore = Guid.NewGuid();
        var inProcessStore = Guid.NewGuid();

        _installations
            .ListForCustomerFeatureAsync(CustomerId, FeatureId, Arg.Any<CancellationToken>())
            .Returns([Installation(StoreId), Installation(otherStore), Installation(inProcessStore)]);

        _endpoints
            .ForInstallationAsync(otherStore, FeatureId, Arg.Any<CancellationToken>())
            .Returns(_endpoint with { StoreId = otherStore, StoreSlug = "borough-books" });

        // The third store has the same Feature installed as a package. An
        // entitlement change must not fail because of it.
        _endpoints
            .ForInstallationAsync(inProcessStore, FeatureId, Arg.Any<CancellationToken>())
            .Returns((ServiceEndpointDescriptor?)null);

        var revoked = await _service.RevokeForCustomerAsync(CustomerId, FeatureId, CancellationToken.None);

        Assert.Equal(2, revoked.Count);
        await _controlPlane.Received(2).RevokeAsync(Arg.Any<ServiceEndpointDescriptor>(), Arg.Any<CancellationToken>());
    }

    private static FeatureInstallation Installation(Guid storeId) =>
        FeatureInstallation.Create(Guid.NewGuid(), Now, storeId, CustomerId, FeatureId, "subscriptions");
}
