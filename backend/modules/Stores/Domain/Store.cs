using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// A customer's store as KNIGHT knows it: an independent Django application with
/// its own domain, database and deployment. KNIGHT holds only the management
/// metadata — never the store's business data, and never a connection to its
/// database (docs/README.md, rules 1 and 3).
///
/// The aggregate owns its credentials so issuing, rotating and revoking always
/// runs through the invariants below rather than by mutating rows directly.
/// </summary>
public sealed class Store : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public string Name { get; private set; }

    public string Slug { get; private set; }

    public string PrimaryDomain { get; private set; }

    public StoreEnvironment Environment { get; private set; }

    public HostingModel HostingModel { get; private set; }

    public StoreStatus Status { get; private set; }

    public IntegrationStatus IntegrationStatus { get; private set; }

    /// <summary>Version the store last reported. Null until the first handshake.</summary>
    public string? ApplicationVersion { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }

    public Guid? ServerId { get; private set; }

    private readonly List<StoreCredential> _credentials = [];

    public IReadOnlyCollection<StoreCredential> Credentials => _credentials.AsReadOnly();

    private Store()
    {
        Name = string.Empty;
        Slug = string.Empty;
        PrimaryDomain = string.Empty;
    }

    private Store(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        string name,
        string slug,
        string primaryDomain,
        StoreEnvironment environment,
        HostingModel hostingModel)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        Name = name;
        Slug = slug;
        PrimaryDomain = primaryDomain;
        Environment = environment;
        HostingModel = hostingModel;
        Status = StoreStatus.Provisioning;
        IntegrationStatus = IntegrationStatus.NotRegistered;
    }

    public static Store Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid customerId,
        string name,
        string slug,
        string primaryDomain,
        StoreEnvironment environment,
        HostingModel hostingModel)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A store must belong to a customer.");
        }

        return new Store(
            id,
            createdAt,
            customerId,
            StoreNormalization.ValidateName(name),
            StoreNormalization.NormalizeSlug(slug),
            StoreNormalization.NormalizeHost(primaryDomain),
            environment,
            hostingModel);
    }

    public void UpdateProfile(string name, string primaryDomain, DateTimeOffset now)
    {
        EnsureNotArchived();
        Name = StoreNormalization.ValidateName(name);
        PrimaryDomain = StoreNormalization.NormalizeHost(primaryDomain);
        MarkUpdated(now);
    }

    public void AssignServer(Guid? serverId, DateTimeOffset now)
    {
        EnsureNotArchived();
        ServerId = serverId;
        MarkUpdated(now);
    }

    // --- Lifecycle -------------------------------------------------------

    public void Activate(DateTimeOffset now)
    {
        if (Status is not (StoreStatus.Provisioning or StoreStatus.Suspended))
        {
            throw DomainException.Conflict($"A store in status '{Status}' cannot be activated.");
        }

        Status = StoreStatus.Active;
        MarkUpdated(now);
    }

    public void Suspend(DateTimeOffset now)
    {
        if (Status is not StoreStatus.Active)
        {
            throw DomainException.Conflict($"A store in status '{Status}' cannot be suspended.");
        }

        Status = StoreStatus.Suspended;
        MarkUpdated(now);
    }

    /// <summary>
    /// Terminal. Archiving revokes every credential: an archived store must not be
    /// able to authenticate or ingest anything (docs/store-integration.md section 2).
    /// </summary>
    public void Archive(DateTimeOffset now)
    {
        if (Status is StoreStatus.Archived)
        {
            throw DomainException.Conflict("The store is already archived.");
        }

        foreach (var credential in _credentials.Where(c => c.IsUsable(now)))
        {
            credential.Revoke(now);
        }

        Status = StoreStatus.Archived;
        IntegrationStatus = IntegrationStatus.Disconnected;
        MarkUpdated(now);
    }

    // --- Integration -----------------------------------------------------

    /// <summary>
    /// Records a successful handshake. The reported environment must match the
    /// registered one: a production store may never report into a non-production
    /// control plane, even with valid credentials (docs/architecture.md section 8).
    /// </summary>
    public void MarkConnected(StoreEnvironment reportedEnvironment, string? applicationVersion, DateTimeOffset now)
    {
        if (Status is StoreStatus.Archived)
        {
            throw DomainException.Conflict("An archived store cannot connect.");
        }

        if (reportedEnvironment != Environment)
        {
            throw DomainException.Conflict(
                $"Environment mismatch: the store reported '{reportedEnvironment}' but is registered as '{Environment}'.");
        }

        IntegrationStatus = IntegrationStatus.Connected;
        ApplicationVersion = StoreNormalization.NormalizeVersion(applicationVersion) ?? ApplicationVersion;
        LastSeenAt = now;
        MarkUpdated(now);
    }

    public void MarkPendingRegistration(DateTimeOffset now)
    {
        EnsureNotArchived();
        IntegrationStatus = IntegrationStatus.Pending;
        MarkUpdated(now);
    }

    public void MarkDegraded(DateTimeOffset now)
    {
        EnsureNotArchived();
        IntegrationStatus = IntegrationStatus.Degraded;
        LastSeenAt = now;
        MarkUpdated(now);
    }

    public void MarkDisconnected(DateTimeOffset now)
    {
        EnsureNotArchived();
        IntegrationStatus = IntegrationStatus.Disconnected;
        MarkUpdated(now);
    }

    // --- Credentials -----------------------------------------------------

    /// <summary>
    /// Issues a credential. The caller generates the secret and passes only its
    /// hash: the plaintext is shown once at the API boundary and never stored
    /// (docs/authentication.md section 2).
    /// </summary>
    public StoreCredential IssueCredential(
        Guid credentialId,
        string clientId,
        string secretHash,
        DateTimeOffset now,
        DateTimeOffset? expiresAt = null)
    {
        EnsureNotArchived();

        var credential = StoreCredential.Issue(credentialId, Id, clientId, secretHash, now, expiresAt);
        _credentials.Add(credential);
        MarkUpdated(now);
        return credential;
    }

    /// <summary>
    /// Rotates a credential: the replacement becomes active immediately while the
    /// previous one stays usable for <paramref name="grace"/>, so a live store does
    /// not lose access the moment an operator clicks rotate (risks.md R8).
    /// </summary>
    public StoreCredential RotateCredential(
        Guid currentCredentialId,
        Guid replacementId,
        string clientId,
        string secretHash,
        TimeSpan grace,
        DateTimeOffset now)
    {
        EnsureNotArchived();

        if (grace < TimeSpan.Zero)
        {
            throw DomainException.Validation("The rotation grace period cannot be negative.");
        }

        var current = _credentials.SingleOrDefault(c => c.Id == currentCredentialId)
            ?? throw DomainException.Conflict("The credential to rotate does not exist on this store.");

        if (!current.IsUsable(now))
        {
            throw DomainException.Conflict("Only an active credential can be rotated.");
        }

        var replacement = StoreCredential.Issue(replacementId, Id, clientId, secretHash, now, null);
        _credentials.Add(replacement);
        current.BeginGracePeriod(now + grace, now);
        MarkUpdated(now);
        return replacement;
    }

    public void RevokeCredential(Guid credentialId, DateTimeOffset now)
    {
        var credential = _credentials.SingleOrDefault(c => c.Id == credentialId)
            ?? throw DomainException.Conflict("The credential does not exist on this store.");

        credential.Revoke(now);
        MarkUpdated(now);
    }

    public bool HasUsableCredential(DateTimeOffset now) => _credentials.Any(c => c.IsUsable(now));

    private void EnsureNotArchived()
    {
        if (Status is StoreStatus.Archived)
        {
            throw DomainException.Conflict("An archived store cannot be modified.");
        }
    }
}
