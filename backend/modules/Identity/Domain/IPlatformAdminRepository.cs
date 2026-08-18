namespace Identity.Domain;

public interface IPlatformAdminRepository
{
    Task<PlatformAdmin?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <param name="normalizedEmail">Must already be normalized via <c>EmailFormat.NormalizeForComparison</c>.</param>
    Task<PlatformAdmin?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task AddAsync(PlatformAdmin admin, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
