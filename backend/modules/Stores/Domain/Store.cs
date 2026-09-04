using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// What one contact with a store did to the link: the state it left the store
/// in, whether the store is still waiting for its domain to be proven, and
/// whether it announced a version KNIGHT had not seen. The caller turns the last
/// of those into a <see cref="StoreDeployment"/>; the aggregate only reports it.
/// </summary>
public sealed record StoreContactOutcome(
    IntegrationStatus Status,
    bool DomainVerificationOutstanding,
    bool VersionChanged,
    string? PreviousVersion);

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

    /// <summary>
    /// SHA-256 thumbprint of the client certificate this store must present, or
    /// null when it authenticates with its credential alone.
    ///
    /// Optional, and only for a store on its own infrastructure. Mutual TLS is a
    /// second, independent factor on the transport: a stolen client secret is
    /// useless without the private key of a certificate KNIGHT was told about in
    /// advance. It is not offered on shared hosting, where the certificate would
    /// have to live beside other customers' stores and would prove rather less
    /// than it appears to (docs/store-integration.md).
    /// </summary>
    public string? MutualTlsThumbprint { get; private set; }

    public bool RequiresMutualTls => MutualTlsThumbprint is not null;

    /// <summary>
    /// The token whoever controls <see cref="PrimaryDomain"/> must publish to
    /// prove they do. Null until an operator asks for one; it is not a secret in
    /// the credential sense — publishing it is the whole point — but it is
    /// single-purpose and is replaced whenever verification is restarted.
    /// </summary>
    public string? DomainVerificationToken { get; private set; }

    public DateTimeOffset? DomainVerificationIssuedAt { get; private set; }

    public DateTimeOffset? DomainVerifiedAt { get; private set; }

    public DomainVerificationMethod? DomainVerificationMethod { get; private set; }

    public bool IsDomainVerified => DomainVerifiedAt is not null;

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

        var host = StoreNormalization.NormalizeHost(primaryDomain);
        if (!string.Equals(host, PrimaryDomain, StringComparison.Ordinal))
        {
            // Ownership was proven for the old domain and says nothing about the
            // new one. Carrying the proof across would let a store move itself to
            // a domain nobody checked.
            PrimaryDomain = host;
            ClearDomainVerification();
        }

        MarkUpdated(now);
    }

    /// <summary>
    /// Moves the store to a different environment.
    ///
    /// This is not a label change. A store's session tokens and its entitlement
    /// signing key are both derived from its environment, so every credential
    /// the store currently holds stops verifying the moment this returns — which
    /// is the property that keeps a staging store from ever reporting into
    /// production, and it must not be quietly weakened just because an operator
    /// picked the wrong value on the creation form.
    ///
    /// So the link is reset rather than left claiming to be connected: the store
    /// must handshake again, and until it does the dashboard says so. Domain
    /// verification is cleared for the same reason it is cleared on a domain
    /// change — the proof was given under the old identity.
    /// </summary>
    public void ChangeEnvironment(StoreEnvironment environment, DateTimeOffset now)
    {
        EnsureNotArchived();

        if (environment == Environment)
        {
            return;
        }

        Environment = environment;

        // Back to square one, deliberately. Anything else would show a link
        // state that the store's own credentials can no longer produce.
        IntegrationStatus = IntegrationStatus.NotRegistered;
        ApplicationVersion = null;
        LastSeenAt = null;
        ClearDomainVerification();

        MarkUpdated(now);
    }

    /// <summary>
    /// Binds the store to a client certificate. Every call it makes from now on
    /// must present that certificate as well as its credential.
    ///
    /// Refused on shared hosting: the certificate would have to be deployed
    /// somewhere a dozen other customers' stores also run, which is not the
    /// property mutual TLS is being bought for.
    /// </summary>
    public void RequireMutualTls(string thumbprint, DateTimeOffset now)
    {
        EnsureNotArchived();

        if (HostingModel is HostingModel.SharedManaged)
        {
            throw DomainException.Conflict(
                "Mutual TLS is only available to a store on dedicated or customer-managed infrastructure.");
        }

        var normalised = (thumbprint ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

        // A SHA-256 thumbprint is 64 hex characters. Anything else is a
        // SHA-1 fingerprint, a truncated copy-paste, or a different field
        // entirely — and a binding nobody can satisfy locks the store out.
        if (normalised.Length != 64 || !normalised.All(Uri.IsHexDigit))
        {
            throw DomainException.Validation("A client certificate thumbprint must be a hex-encoded sha-256.");
        }

        MutualTlsThumbprint = normalised;
        MarkUpdated(now);
    }

    /// <summary>Stops requiring a client certificate. The credential alone is enough again.</summary>
    public void ClearMutualTls(DateTimeOffset now)
    {
        EnsureNotArchived();
        MutualTlsThumbprint = null;
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
    ///
    /// A store whose primary domain has not been proven does not reach
    /// <see cref="IntegrationStatus.Connected"/>; it waits in
    /// <see cref="IntegrationStatus.Pending"/>. Credentials prove that the caller
    /// holds a secret KNIGHT issued — they say nothing about who controls the
    /// domain that traffic will be sent to, and KNIGHT polls that domain
    /// (docs/security-threat-model.md).
    /// </summary>
    public StoreContactOutcome CompleteHandshake(
        StoreEnvironment reportedEnvironment,
        string? applicationVersion,
        bool requireDomainVerification,
        DateTimeOffset now)
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

        return RecordObservation(StoreHealthStatus.Healthy, applicationVersion, requireDomainVerification, now);
    }

    /// <summary>
    /// Applies what a health observation saw — whether KNIGHT polled the store or
    /// the store sent a heartbeat. The mapping from one observation to the
    /// settled link state lives here so a poller and an ingestion endpoint cannot
    /// disagree about what "degraded" means.
    /// </summary>
    public StoreContactOutcome RecordObservation(
        StoreHealthStatus observed,
        string? applicationVersion,
        bool requireDomainVerification,
        DateTimeOffset now)
    {
        EnsureNotArchived();

        var previousVersion = ApplicationVersion;
        var reported = StoreNormalization.NormalizeVersion(applicationVersion);
        if (reported is not null)
        {
            ApplicationVersion = reported;
        }

        var outstanding = requireDomainVerification && !IsDomainVerified;

        IntegrationStatus = observed switch
        {
            // An unanswered poll is the one observation that does not mean the
            // store spoke to us, so it neither advances LastSeenAt below nor
            // clears an outstanding verification.
            StoreHealthStatus.Unreachable => IntegrationStatus.Disconnected,
            _ when outstanding => IntegrationStatus.Pending,
            StoreHealthStatus.Healthy => IntegrationStatus.Connected,
            _ => IntegrationStatus.Degraded,
        };

        if (observed is not StoreHealthStatus.Unreachable)
        {
            LastSeenAt = now;
        }

        MarkUpdated(now);

        var changed = reported is not null && !string.Equals(reported, previousVersion, StringComparison.Ordinal);
        return new StoreContactOutcome(IntegrationStatus, outstanding, changed, changed ? previousVersion : null);
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

    // --- Domain ownership ------------------------------------------------

    /// <summary>
    /// Starts (or restarts) proof of ownership for the primary domain. Any
    /// earlier proof is dropped: the point of re-issuing is that the previous
    /// answer is no longer trusted.
    /// </summary>
    public void IssueDomainVerification(string token, DateTimeOffset now)
    {
        EnsureNotArchived();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw DomainException.Validation("A domain verification token is required.");
        }

        DomainVerificationToken = token.Trim();
        DomainVerificationIssuedAt = now;
        DomainVerifiedAt = null;
        DomainVerificationMethod = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records that the issued token was found published on the domain. Verifying
    /// without an outstanding token is refused rather than treated as success:
    /// there would be nothing that could have been checked.
    /// </summary>
    public void MarkDomainVerified(DomainVerificationMethod method, DateTimeOffset now)
    {
        EnsureNotArchived();

        if (DomainVerificationToken is null)
        {
            throw DomainException.Conflict("No domain verification has been started for this store.");
        }

        DomainVerifiedAt = now;
        DomainVerificationMethod = method;
        MarkUpdated(now);
    }

    private void ClearDomainVerification()
    {
        DomainVerificationToken = null;
        DomainVerificationIssuedAt = null;
        DomainVerifiedAt = null;
        DomainVerificationMethod = null;
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
        DateTimeOffset now,
        DateTimeOffset? replacementExpiresAt = null)
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

        // The replacement carries its own expiry when rotation is lifetime-driven
        // (rotate-on-handshake), so it too comes due for rotation in turn; an
        // operator-initiated rotation passes none and the replacement is open-ended.
        var replacement = StoreCredential.Issue(replacementId, Id, clientId, secretHash, now, replacementExpiresAt);
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
