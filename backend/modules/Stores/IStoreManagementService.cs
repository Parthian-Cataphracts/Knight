using Stores.Domain;

namespace Stores;

/// <summary>
/// <paramref name="ServerId"/> is optional: a store may be registered before
/// anybody has decided which machine it will run on. When it is supplied the
/// placement is checked exactly as it is on a later move - a dedicated machine
/// belonging to somebody else is refused at registration rather than accepted
/// and discovered afterwards.
/// </summary>
public sealed record CreateStoreInput(
    Guid CustomerId,
    string Name,
    string Slug,
    string PrimaryDomain,
    StoreEnvironment Environment,
    HostingModel HostingModel,
    Guid? ServerId = null);

/// <summary>
/// <paramref name="Environment"/> is null when the caller is not changing it.
/// Distinguishing "leave it alone" from "set it to Development" matters here,
/// because setting it resets the store's integration link
/// (<see cref="Domain.Store.ChangeEnvironment"/>).
/// </summary>
public sealed record UpdateStoreInput(
    string Name,
    string PrimaryDomain,
    Domain.StoreEnvironment? Environment = null);

public sealed record StoreListQuery(int Page, int PageSize, Guid? CustomerId, StoreEnvironment? Environment, StoreStatus? Status);

public sealed record StorePage(IReadOnlyCollection<Store> Items, int Page, int PageSize, long TotalCount);

/// <summary>
/// The plaintext secret exists only in this result, on the way to the response
/// that shows it once. It is never stored, logged or audited.
/// </summary>
public sealed record IssuedStoreCredential(Guid CredentialId, string ClientId, string ClientSecret, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>
/// Store registration and credential management for the dashboard.
/// </summary>
public interface IStoreManagementService
{
    Task<Store> CreateAsync(CreateStoreInput input, CancellationToken cancellationToken);

    Task<Store?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<StorePage> ListAsync(StoreListQuery query, CancellationToken cancellationToken);

    Task<Store> UpdateAsync(Guid id, UpdateStoreInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Places the store on a server, or takes it off one with a null id.
    ///
    /// Its own operation rather than a field on the update, for the reason the
    /// retention override has its own route: where a customer's store runs is a
    /// fact worth an audit entry that says so, and a field on a profile update
    /// is a field every caller that edits a name has to remember to send back or
    /// silently erase.
    /// </summary>
    Task<Store> AssignServerAsync(Guid id, Guid? serverId, CancellationToken cancellationToken);

    Task<Store> ActivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Store> SuspendAsync(Guid id, CancellationToken cancellationToken);

    Task<Store> ArchiveAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Binds the store to a client certificate, or clears the binding when
    /// <paramref name="thumbprint"/> is null. Only available to a store on
    /// dedicated or customer-managed infrastructure.
    /// </summary>
    Task<Store> SetMutualTlsAsync(Guid storeId, string? thumbprint, CancellationToken cancellationToken);

    Task<IssuedStoreCredential> IssueCredentialAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>Issues a replacement and leaves the previous secret usable for the configured grace window.</summary>
    Task<IssuedStoreCredential> RotateCredentialAsync(Guid storeId, Guid credentialId, CancellationToken cancellationToken);

    Task RevokeCredentialAsync(Guid storeId, Guid credentialId, CancellationToken cancellationToken);
}
