using Stores.Domain;

namespace Stores;

public sealed record CreateStoreInput(
    Guid CustomerId,
    string Name,
    string Slug,
    string PrimaryDomain,
    StoreEnvironment Environment,
    HostingModel HostingModel);

/// <summary>
/// <paramref name="Environment"/> is null when the caller is not changing it.
/// Distinguishing "leave it alone" from "set it to Development" matters here,
/// because setting it resets the store's integration link
/// (<see cref="Domain.Store.ChangeEnvironment"/>).
/// </summary>
public sealed record UpdateStoreInput(
    string Name,
    string PrimaryDomain,
    Guid? ServerId,
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

    Task<Store> ActivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Store> SuspendAsync(Guid id, CancellationToken cancellationToken);

    Task<Store> ArchiveAsync(Guid id, CancellationToken cancellationToken);

    Task<IssuedStoreCredential> IssueCredentialAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>Issues a replacement and leaves the previous secret usable for the configured grace window.</summary>
    Task<IssuedStoreCredential> RotateCredentialAsync(Guid storeId, Guid credentialId, CancellationToken cancellationToken);

    Task RevokeCredentialAsync(Guid storeId, Guid credentialId, CancellationToken cancellationToken);
}
