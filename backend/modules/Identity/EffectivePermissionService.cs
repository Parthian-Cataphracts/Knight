using Identity.Domain;

namespace Identity;

public sealed class EffectivePermissionService : IEffectivePermissionService
{
    private readonly ITenantUserRoleRepository _repository;

    public EffectivePermissionService(ITenantUserRoleRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(Guid tenantId, Guid tenantUserId, CancellationToken cancellationToken) =>
        _repository.GetEffectivePermissionKeysAsync(tenantId, tenantUserId, cancellationToken);
}
