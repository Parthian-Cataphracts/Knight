using FeatureManagement;
using Knight.Contracts.Common;
using Knight.Contracts.Platform;
using Tenancy;
using Tenancy.Domain;

namespace Knight.Api.Endpoints;

/// <summary>
/// Platform-authorized tenant lifecycle and configuration management. Every route
/// here requires the "PlatformAdminOnly" policy — anonymous callers and tenant
/// users are denied (see Program.cs authorization policy registration).
/// </summary>
public static class PlatformTenantEndpoints
{
    public static void MapPlatformTenantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Tenants");

        group.MapPost("/", async (CreateTenantRequest request, ITenantManagementService service, CancellationToken cancellationToken) =>
        {
            var tenant = await service.CreateAsync(new CreateTenantInput(request.Name, request.Slug, request.TimeZone, request.DefaultCurrency), cancellationToken);
            return Results.Created($"/api/platform/tenants/{tenant.Id}", ToResponse(tenant));
        });

        group.MapGet("/", async (int? page, int? pageSize, ITenantManagementService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(page ?? 1, pageSize ?? 20, cancellationToken);
            var response = PagedResponse<TenantResponse>.Create(
                result.Items.Select(ToResponse).ToArray(), result.Page, result.PageSize, result.TotalCount);

            return Results.Ok(response);
        });

        group.MapGet("/{id:guid}", async (Guid id, ITenantManagementService service, CancellationToken cancellationToken) =>
        {
            var tenant = await service.GetAsync(id, cancellationToken);
            return tenant is null ? Results.NotFound() : Results.Ok(ToResponse(tenant));
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateTenantRequest request, ITenantManagementService service, CancellationToken cancellationToken) =>
        {
            var tenant = await service.UpdateAsync(id, new UpdateTenantInput(request.Name, request.TimeZone, request.DefaultCurrency), cancellationToken);
            return Results.Ok(ToResponse(tenant));
        });

        group.MapPost("/{id:guid}/activate", async (Guid id, ITenantManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ActivateAsync(id, cancellationToken))));

        group.MapPost("/{id:guid}/suspend", async (Guid id, ITenantManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.SuspendAsync(id, cancellationToken))));

        group.MapPost("/{id:guid}/archive", async (Guid id, ITenantManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ArchiveAsync(id, cancellationToken))));

        group.MapPost("/{id:guid}/domains", async (Guid id, AddTenantDomainRequest request, ITenantManagementService service, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<TenantDomainType>(request.Type, ignoreCase: true, out var type))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["type"] = [$"'{request.Type}' is not a recognized domain type."]
                });
            }

            var domain = await service.AddDomainAsync(id, new AddTenantDomainInput(request.Host, type, request.MakePrimary), cancellationToken);
            return Results.Ok(ToResponse(domain));
        });

        group.MapDelete("/{id:guid}/domains/{domainId:guid}", async (Guid id, Guid domainId, ITenantManagementService service, CancellationToken cancellationToken) =>
        {
            await service.RemoveDomainAsync(id, domainId, cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/domains/{domainId:guid}/primary", async (Guid id, Guid domainId, ITenantManagementService service, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.SetPrimaryDomainAsync(id, domainId, cancellationToken))));

        group.MapPost("/{id:guid}/features/{featureKey}/enable", async (Guid id, string featureKey, ITenantFeatureManagementService service, CancellationToken cancellationToken) =>
        {
            await service.EnableAsync(id, featureKey, cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/features/{featureKey}/disable", async (Guid id, string featureKey, ITenantFeatureManagementService service, CancellationToken cancellationToken) =>
        {
            await service.DisableAsync(id, featureKey, cancellationToken);
            return Results.NoContent();
        });
    }

    private static TenantResponse ToResponse(Tenant tenant) => new()
    {
        Id = tenant.Id,
        Name = tenant.Name,
        Slug = tenant.Slug,
        Status = tenant.Status.ToString(),
        TimeZone = tenant.TimeZone,
        DefaultCurrency = tenant.DefaultCurrency,
        Domains = tenant.Domains.Select(ToResponse).ToArray(),
        CreatedAt = tenant.CreatedAt,
        UpdatedAt = tenant.UpdatedAt
    };

    private static TenantDomainResponse ToResponse(TenantDomain domain) => new()
    {
        Id = domain.Id,
        Host = domain.Host,
        Type = domain.Type.ToString(),
        IsPrimary = domain.IsPrimary,
        VerificationStatus = domain.VerificationStatus.ToString()
    };
}
