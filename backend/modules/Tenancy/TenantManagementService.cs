using Knight.Application.Abstractions.Auditing;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Tenancy.Domain;

namespace Tenancy;

/// <summary>
/// Default <see cref="ITenantManagementService"/>. Every mutation is audited with
/// the acting principal, matching docs/architecture/authorization.md.
/// </summary>
public sealed class TenantManagementService : ITenantManagementService
{
    private const int MaxPageSize = 100;

    private readonly ITenantRepository _tenantRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditLogger _auditLogger;

    public TenantManagementService(
        ITenantRepository tenantRepository,
        IDateTimeProvider dateTimeProvider,
        ICurrentUser currentUser,
        IAuditLogger auditLogger)
    {
        _tenantRepository = tenantRepository;
        _dateTimeProvider = dateTimeProvider;
        _currentUser = currentUser;
        _auditLogger = auditLogger;
    }

    public async Task<Tenant> CreateAsync(CreateTenantInput input, CancellationToken cancellationToken)
    {
        var existing = await _tenantRepository.GetBySlugAsync(SlugForLookup(input.Slug), cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException($"A tenant with slug '{input.Slug}' already exists.");
        }

        var now = _dateTimeProvider.UtcNow;
        var tenant = Tenant.Create(Guid.NewGuid(), now, input.Name, input.Slug, input.TimeZone, input.DefaultCurrency);

        await _tenantRepository.AddAsync(tenant, cancellationToken);

        await AuditAsync("TenantCreated", tenant.Id, tenant.Id, cancellationToken, new Dictionary<string, string>
        {
            ["name"] = tenant.Name,
            ["slug"] = tenant.Slug
        });

        return tenant;
    }

    public Task<Tenant?> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _tenantRepository.GetByIdAsync(tenantId, cancellationToken);

    public async Task<TenantListResult> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var boundedPage = Math.Max(page, 1);
        var boundedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, total) = await _tenantRepository.ListAsync(boundedPage, boundedPageSize, cancellationToken);
        return new TenantListResult(items, total, boundedPage, boundedPageSize);
    }

    public async Task<Tenant> UpdateAsync(Guid tenantId, UpdateTenantInput input, CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        tenant.UpdateProfile(input.Name, input.TimeZone, input.DefaultCurrency, now);
        await _tenantRepository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantUpdated", tenant.Id, tenant.Id, cancellationToken);
        return tenant;
    }

    public async Task<Tenant> ActivateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        tenant.Activate(_dateTimeProvider.UtcNow);
        await _tenantRepository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantActivated", tenant.Id, tenant.Id, cancellationToken);
        return tenant;
    }

    public async Task<Tenant> SuspendAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        tenant.Suspend(_dateTimeProvider.UtcNow);
        await _tenantRepository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantSuspended", tenant.Id, tenant.Id, cancellationToken);
        return tenant;
    }

    public async Task<Tenant> ArchiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        tenant.Archive(_dateTimeProvider.UtcNow);
        await _tenantRepository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantArchived", tenant.Id, tenant.Id, cancellationToken);
        return tenant;
    }

    public async Task<TenantDomain> AddDomainAsync(Guid tenantId, AddTenantDomainInput input, CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        var domain = tenant.AddDomain(Guid.NewGuid(), input.Host, input.Type, input.MakePrimary, now);
        await _tenantRepository.RegisterNewDomainAsync(domain, cancellationToken);

        try
        {
            await _tenantRepository.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            throw new ConflictException($"Host '{domain.Host}' is already mapped to another tenant.");
        }

        await AuditAsync("TenantDomainAdded", tenant.Id, tenant.Id, cancellationToken, new Dictionary<string, string>
        {
            ["host"] = domain.Host,
            ["type"] = domain.Type.ToString()
        });

        return domain;
    }

    public async Task RemoveDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        var host = tenant.Domains.FirstOrDefault(d => d.Id == domainId)?.Host;

        tenant.RemoveDomain(domainId, _dateTimeProvider.UtcNow);
        await _tenantRepository.SaveChangesAsync(cancellationToken);

        await AuditAsync("TenantDomainRemoved", tenant.Id, tenant.Id, cancellationToken,
            host is null ? null : new Dictionary<string, string> { ["host"] = host });
    }

    public async Task<TenantDomain> SetPrimaryDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken)
    {
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        tenant.SetPrimaryDomain(domainId, _dateTimeProvider.UtcNow);
        await _tenantRepository.SaveChangesAsync(cancellationToken);

        var domain = tenant.Domains.First(d => d.Id == domainId);

        await AuditAsync("TenantPrimaryDomainChanged", tenant.Id, tenant.Id, cancellationToken, new Dictionary<string, string>
        {
            ["host"] = domain.Host,
            ["type"] = domain.Type.ToString()
        });

        return domain;
    }

    private async Task<Tenant> RequireTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _tenantRepository.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

    private static string SlugForLookup(string slug) => slug.Trim().ToLowerInvariant();

    private async Task AuditAsync(
        string action,
        Guid tenantId,
        Guid entityId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        await _auditLogger.RecordAsync(new AuditEntry
        {
            ActorUserId = _currentUser.UserId,
            ActorType = _currentUser.PrincipalType == PrincipalType.PlatformAdmin
                ? AuditActorType.PlatformAdmin
                : AuditActorType.System,
            TenantId = tenantId,
            Action = action,
            EntityType = nameof(Tenant),
            EntityId = entityId.ToString(),
            Metadata = metadata
        }, cancellationToken);
    }
}
