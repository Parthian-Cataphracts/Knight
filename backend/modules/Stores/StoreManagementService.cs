using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Options;
using Stores.Domain;

namespace Stores;

/// <summary>
/// Store registration and credential management.
///
/// Credentials are the sensitive part: the secret is generated here, hashed
/// immediately, and returned to the caller exactly once. Neither the plaintext
/// nor the hash ever reaches an audit entry — the entry records that a
/// credential was issued, to which store, by whom
/// (docs/authentication.md section 2).
/// </summary>
internal sealed class StoreManagementService : IStoreManagementService
{
    private const int MaxPageSize = 100;

    private readonly IStoreRepository _stores;
    private readonly ISecureTokenFactory _secrets;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly StoreOptions _options;

    public StoreManagementService(
        IStoreRepository stores,
        ISecureTokenFactory secrets,
        IAuditTrail audit,
        IDateTimeProvider clock,
        IOptions<StoreOptions> options)
    {
        _stores = stores;
        _secrets = secrets;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<Store> CreateAsync(CreateStoreInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var slug = StoreNormalization.NormalizeSlug(input.Slug);
        var host = StoreNormalization.NormalizeHost(input.PrimaryDomain);

        if (await _stores.GetBySlugAsync(slug, cancellationToken) is not null)
        {
            throw new ConflictException($"Slug '{slug}' is already taken.");
        }

        if (await _stores.GetByPrimaryDomainAsync(host, cancellationToken) is not null)
        {
            throw new ConflictException($"Domain '{host}' is already registered to another store.");
        }

        var store = Store.Create(
            Guid.NewGuid(),
            now,
            input.CustomerId,
            input.Name,
            input.Slug,
            input.PrimaryDomain,
            input.Environment,
            input.HostingModel);

        await _stores.AddAsync(store, cancellationToken);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.created",
            nameof(Store),
            store.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: Snapshot(store));

        return store;
    }

    public Task<Store?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _stores.GetByIdAsync(id, cancellationToken);

    public async Task<StorePage> ListAsync(StoreListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > MaxPageSize ? 25 : query.PageSize;

        var (items, total) = await _stores.ListAsync(page, pageSize, query.CustomerId, query.Environment, query.Status, cancellationToken);
        return new StorePage(items, page, pageSize, total);
    }

    public async Task<Store> UpdateAsync(Guid id, UpdateStoreInput input, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(id, cancellationToken);
        var before = Snapshot(store);
        var now = _clock.UtcNow;

        var host = StoreNormalization.NormalizeHost(input.PrimaryDomain);
        var owner = await _stores.GetByPrimaryDomainAsync(host, cancellationToken);
        if (owner is not null && owner.Id != store.Id)
        {
            throw new ConflictException($"Domain '{host}' is already registered to another store.");
        }

        store.UpdateProfile(input.Name, input.PrimaryDomain, now);
        store.AssignServer(input.ServerId, now);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.updated",
            nameof(Store),
            store.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            before,
            Snapshot(store));

        return store;
    }

    public Task<Store> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "store.activated", (store, now) => store.Activate(now), cancellationToken);

    public Task<Store> SuspendAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "store.suspended", (store, now) => store.Suspend(now), cancellationToken);

    public Task<Store> ArchiveAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "store.archived", (store, now) => store.Archive(now), cancellationToken);

    public async Task<IssuedStoreCredential> IssueCredentialAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(storeId, cancellationToken);
        var now = _clock.UtcNow;

        var clientId = BuildClientId(store);
        var secret = _secrets.Generate();
        var expiresAt = _options.CredentialLifetime is { } lifetime ? now.Add(lifetime) : (DateTimeOffset?)null;

        var credential = store.IssueCredential(Guid.NewGuid(), clientId, secret.Hash, now, expiresAt);
        _stores.RegisterNewCredential(credential);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.credential.issued",
            nameof(StoreCredential),
            credential.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new { storeId = store.Id, clientId, expiresAt });

        return new IssuedStoreCredential(credential.Id, clientId, secret.RawValue, credential.CreatedAt, credential.ExpiresAt);
    }

    public async Task<IssuedStoreCredential> RotateCredentialAsync(Guid storeId, Guid credentialId, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(storeId, cancellationToken);
        var now = _clock.UtcNow;

        var clientId = BuildClientId(store);
        var secret = _secrets.Generate();

        var replacement = store.RotateCredential(
            credentialId,
            Guid.NewGuid(),
            clientId,
            secret.Hash,
            _options.RotationGracePeriod,
            now);

        _stores.RegisterNewCredential(replacement);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.credential.rotated",
            nameof(StoreCredential),
            replacement.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            previousValue: new { credentialId },
            newValue: new { storeId = store.Id, clientId, graceUntil = now + _options.RotationGracePeriod });

        return new IssuedStoreCredential(replacement.Id, clientId, secret.RawValue, replacement.CreatedAt, replacement.ExpiresAt);
    }

    public async Task RevokeCredentialAsync(Guid storeId, Guid credentialId, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(storeId, cancellationToken);

        store.RevokeCredential(credentialId, _clock.UtcNow);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.credential.revoked",
            nameof(StoreCredential),
            credentialId.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new { storeId = store.Id });
    }

    private async Task<Store> TransitionAsync(
        Guid id,
        string action,
        Action<Store, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var store = await RequireAsync(id, cancellationToken);
        var before = Snapshot(store);

        transition(store, _clock.UtcNow);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            action,
            nameof(Store),
            store.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            before,
            Snapshot(store));

        return store;
    }

    private async Task<Store> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _stores.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Store '{id}' was not found.");

    /// <summary>
    /// Client ids are readable on purpose — they show up in store configuration
    /// and support conversations — but carry a random tail so that knowing a
    /// store's slug does not tell anyone what its client id is.
    /// </summary>
    private static string BuildClientId(Store store) =>
        $"knight-{store.Slug}-{Guid.NewGuid().ToString("n")[..12]}";

    private static object Snapshot(Store store) => new
    {
        store.Name,
        store.Slug,
        store.PrimaryDomain,
        Environment = store.Environment.ToString(),
        HostingModel = store.HostingModel.ToString(),
        Status = store.Status.ToString(),
        IntegrationStatus = store.IntegrationStatus.ToString(),
        store.ServerId,
    };
}
