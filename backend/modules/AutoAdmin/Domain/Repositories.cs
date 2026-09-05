namespace AutoAdmin.Domain;

/// <summary>Persistence for a customer's Automatic Admin settings.</summary>
public interface IAutoAdminSettingsRepository
{
    Task<AutoAdminSettings?> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    Task AddAsync(AutoAdminSettings settings, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Persistence for content jobs and their drafts and publications.</summary>
public interface IContentJobRepository
{
    Task<ContentJob?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(ContentJob job, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ContentJob>> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
