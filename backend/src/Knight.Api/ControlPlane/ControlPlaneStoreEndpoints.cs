using AccessControl.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
using Stores;
using Stores.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Store registration and credential management (docs/api-contracts.md
/// section 2).
///
/// Issuing and rotating a credential are separate permissions from managing the
/// store itself: they hand out something that can authenticate as the store, and
/// that is a different blast radius from renaming it.
/// </summary>
public static class ControlPlaneStoreEndpoints
{
    public static void MapControlPlaneStoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/stores")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Stores");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? customerId,
            string? environment,
            string? status,
            IStoreManagementService service,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<StoreEnvironment>(environment, out var parsedEnvironment))
            {
                return ValidationProblem("environment", $"'{environment}' is not a recognised environment.");
            }

            if (!TryParse<StoreStatus>(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised store status.");
            }

            var result = await service.ListAsync(
                new StoreListQuery(page ?? 1, pageSize ?? 25, customerId, parsedEnvironment, parsedStatus),
                cancellationToken);

            return Results.Ok(PagedResponse<StoreResponse>.Create(
                result.Items.Select(store => ToResponse(store, clock.UtcNow)).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IStoreManagementService service,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var store = await service.GetAsync(id, cancellationToken);
            return store is null ? Results.NotFound() : Results.Ok(ToResponse(store, clock.UtcNow));
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapPost("/", async (
            CreateStoreRequest request,
            IStoreManagementService service,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<StoreEnvironment>(request.Environment, ignoreCase: true, out var environment))
            {
                return ValidationProblem("environment", $"'{request.Environment}' is not a recognised environment.");
            }

            if (!Enum.TryParse<HostingModel>(request.HostingModel, ignoreCase: true, out var hostingModel))
            {
                return ValidationProblem("hostingModel", $"'{request.HostingModel}' is not a recognised hosting model.");
            }

            var store = await service.CreateAsync(
                new CreateStoreInput(request.CustomerId, request.Name, request.Slug, request.PrimaryDomain, environment, hostingModel),
                cancellationToken);

            return Results.Created($"/api/v1/stores/{store.Id}", ToResponse(store, clock.UtcNow));
        }).RequirePermission(ControlPlanePermissions.StoreCreate);

        group.MapPatch("/{id:guid}", async (
            Guid id,
            UpdateStoreRequest request,
            IStoreManagementService service,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var store = await service.UpdateAsync(id, new UpdateStoreInput(request.Name, request.PrimaryDomain, request.ServerId), cancellationToken);
            return Results.Ok(ToResponse(store, clock.UtcNow));
        }).RequirePermission(ControlPlanePermissions.StoreManage);

        group.MapPost("/{id:guid}/activate", async (Guid id, IStoreManagementService service, IDateTimeProvider clock, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ActivateAsync(id, cancellationToken), clock.UtcNow)))
            .RequirePermission(ControlPlanePermissions.StoreManage);

        group.MapPost("/{id:guid}/suspend", async (Guid id, IStoreManagementService service, IDateTimeProvider clock, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.SuspendAsync(id, cancellationToken), clock.UtcNow)))
            .RequirePermission(ControlPlanePermissions.StoreManage);

        group.MapPost("/{id:guid}/archive", async (Guid id, IStoreManagementService service, IDateTimeProvider clock, CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.ArchiveAsync(id, cancellationToken), clock.UtcNow)))
            .RequirePermission(ControlPlanePermissions.StoreManage);

        group.MapPost("/{id:guid}/credentials", async (Guid id, IStoreManagementService service, CancellationToken cancellationToken) =>
        {
            var issued = await service.IssueCredentialAsync(id, cancellationToken);
            return Results.Created($"/api/v1/stores/{id}/credentials/{issued.CredentialId}", ToResponse(issued));
        }).RequirePermission(ControlPlanePermissions.StoreCredentialsManage);

        group.MapPost("/{id:guid}/credentials/{credentialId:guid}/rotate", async (
            Guid id,
            Guid credentialId,
            IStoreManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await service.RotateCredentialAsync(id, credentialId, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.StoreCredentialsManage);

        group.MapDelete("/{id:guid}/credentials/{credentialId:guid}", async (
            Guid id,
            Guid credentialId,
            IStoreManagementService service,
            CancellationToken cancellationToken) =>
        {
            await service.RevokeCredentialAsync(id, credentialId, cancellationToken);
            return Results.NoContent();
        }).RequirePermission(ControlPlanePermissions.StoreCredentialsManage);
    }

    private static bool TryParse<TEnum>(string? value, out TEnum? parsed) where TEnum : struct, Enum
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IssuedStoreCredentialResponse ToResponse(IssuedStoreCredential issued) => new()
    {
        Id = issued.CredentialId,
        ClientId = issued.ClientId,
        ClientSecret = issued.ClientSecret,
        CreatedAt = issued.CreatedAt,
        ExpiresAt = issued.ExpiresAt,
    };

    internal static StoreResponse ToResponse(Store store, DateTimeOffset now) => new()
    {
        Id = store.Id,
        CustomerId = store.CustomerId,
        Name = store.Name,
        Slug = store.Slug,
        PrimaryDomain = store.PrimaryDomain,
        Environment = store.Environment.ToString(),
        HostingModel = store.HostingModel.ToString(),
        Status = store.Status.ToString(),
        IntegrationStatus = store.IntegrationStatus.ToString(),
        ApplicationVersion = store.ApplicationVersion,
        LastSeenAt = store.LastSeenAt,
        ServerId = store.ServerId,

        // Credentials are described by state, never by value: the response
        // carries no secret and no hash.
        Credentials = store.Credentials.Select(credential => new StoreCredentialResponse
        {
            Id = credential.Id,
            ClientId = credential.ClientId,
            State = credential.StateAt(now).ToString(),
            CreatedAt = credential.CreatedAt,
            ExpiresAt = credential.ExpiresAt,
            RotatedAt = credential.RotatedAt,
            RevokedAt = credential.RevokedAt,
            LastUsedAt = credential.LastUsedAt,
        }).ToArray(),

        CreatedAt = store.CreatedAt,
        UpdatedAt = store.UpdatedAt,
    };
}
