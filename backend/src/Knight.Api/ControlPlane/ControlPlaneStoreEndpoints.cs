using AccessControl.Domain;
using Ingestion;
using Knight.Application.Abstractions.ControlPlane;
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
            ILabelReader labels,
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

            // The owning customer's name is resolved for the whole page at once.
            var names = await labels.CustomerNamesAsync(
                result.Items.Select(store => store.CustomerId).Distinct().ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<StoreResponse>.Create(
                result.Items.Select(store => ToResponse(store, clock.UtcNow, names.GetValueOrDefault(store.CustomerId))).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IStoreManagementService service,
            ILabelReader labels,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var store = await service.GetAsync(id, cancellationToken);
            if (store is null)
            {
                return Results.NotFound();
            }

            var names = await labels.CustomerNamesAsync([store.CustomerId], cancellationToken);
            return Results.Ok(ToResponse(store, clock.UtcNow, names.GetValueOrDefault(store.CustomerId)));
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

        // --- The integration link -------------------------------------------

        group.MapGet("/{id:guid}/health", async (
            Guid id,
            int? limit,
            IStoreManagementService stores,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
        {
            var store = await stores.GetAsync(id, cancellationToken);
            if (store is null)
            {
                return Results.NotFound();
            }

            var history = await integration.ListHealthChecksAsync(id, limit ?? 20, cancellationToken);
            var checks = history.Select(ToResponse).ToArray();

            return Results.Ok(new StoreHealthResponse
            {
                StoreId = store.Id,
                IntegrationStatus = store.IntegrationStatus.ToString(),
                LastSeenAt = store.LastSeenAt,
                ApplicationVersion = store.ApplicationVersion,
                Latest = checks.FirstOrDefault(),
                History = checks,
            });
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapGet("/{id:guid}/deployments", async (
            Guid id,
            int? limit,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
        {
            var deployments = await integration.ListDeploymentsAsync(id, limit ?? 25, cancellationToken);
            return Results.Ok(deployments.Select(ToResponse).ToArray());
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapGet("/{id:guid}/events", async (
            Guid id,
            int? page,
            int? pageSize,
            IIngestionService ingestion,
            CancellationToken cancellationToken) =>
        {
            var (items, total) = await ingestion.ListEventsAsync(id, page ?? 1, pageSize ?? 25, cancellationToken);

            return Results.Ok(PagedResponse<StoreEventResponse>.Create(
                items.Select(ToResponse).ToArray(),
                page ?? 1,
                pageSize ?? 25,
                total));
        }).RequirePermission(ControlPlanePermissions.StoreView);

        // Raw, ungrouped, and deliberately labelled as such: grouping arrives in
        // phase 5, and showing the stream is more honest than showing a screen
        // that pretends counts exist.
        group.MapGet("/{id:guid}/errors", async (
            Guid id,
            int? page,
            int? pageSize,
            IIngestionService ingestion,
            CancellationToken cancellationToken) =>
        {
            var (items, total) = await ingestion.ListErrorsAsync(id, page ?? 1, pageSize ?? 25, cancellationToken);

            return Results.Ok(PagedResponse<StoreErrorEventResponse>.Create(
                items.Select(ToResponse).ToArray(),
                page ?? 1,
                pageSize ?? 25,
                total));
        }).RequirePermission(ControlPlanePermissions.ErrorsView);

        // --- Domain ownership ------------------------------------------------

        group.MapGet("/{id:guid}/domain-verification", async (
            Guid id,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
        {
            var challenge = await integration.GetDomainVerificationAsync(id, cancellationToken);
            return challenge is null ? Results.NoContent() : Results.Ok(ToResponse(challenge));
        }).RequirePermission(ControlPlanePermissions.StoreView);

        group.MapPost("/{id:guid}/domain-verification", async (
            Guid id,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await integration.StartDomainVerificationAsync(id, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.StoreManage);

        group.MapPost("/{id:guid}/domain-verification/verify", async (
            Guid id,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
        {
            var result = await integration.VerifyDomainAsync(id, cancellationToken);

            return Results.Ok(new DomainVerificationAttemptResponse
            {
                Verified = result.Verified,
                Method = result.Method,
                Detail = result.Detail,
                VerifiedAt = result.VerifiedAt,
            });
        }).RequirePermission(ControlPlanePermissions.StoreManage);
    }

    /// <summary>
    /// The store's dependency and feature blocks are re-emitted as JSON rather
    /// than as a string, so the dashboard receives the document the store sent
    /// instead of a quoted blob it has to parse itself.
    /// </summary>
    private static StoreHealthCheckResponse ToResponse(StoreHealthCheck check) => new()
    {
        Id = check.Id,
        CheckedAt = check.CheckedAt,
        Status = check.Status.ToString(),
        Source = check.Source.ToString(),
        ResponseTimeMs = check.ResponseTimeMs,
        ReportedVersion = check.ReportedVersion,
        Dependencies = AsDocument(check.Dependencies),
        ReportedFeatures = AsDocument(check.ReportedFeatures),
        Detail = check.Detail,
    };

    private static StoreDeploymentResponse ToResponse(StoreDeployment deployment) => new()
    {
        Id = deployment.Id,
        Version = deployment.Version,
        PreviousVersion = deployment.PreviousVersion,
        DeployedAt = deployment.DeployedAt,
        DetectedAt = deployment.DetectedAt,
        Source = deployment.Source.ToString(),
        Status = deployment.Status.ToString(),
        Notes = deployment.Notes,
    };

    private static DomainVerificationResponse ToResponse(DomainVerificationChallenge challenge) => new()
    {
        StoreId = challenge.StoreId,
        Domain = challenge.Domain,
        Token = challenge.Token,
        HttpPath = challenge.HttpPath,
        DnsRecordName = challenge.DnsRecordName,
        IssuedAt = challenge.IssuedAt,
        VerifiedAt = challenge.VerifiedAt,
    };

    private static StoreErrorEventResponse ToResponse(Ingestion.Domain.StoreErrorEvent error) => new()
    {
        Id = error.Id,
        StoreId = error.StoreId,
        OccurredAt = error.OccurredAt,
        ReceivedAt = error.ReceivedAt,
        Environment = error.Environment,
        StoreVersion = error.StoreVersion,
        ExceptionType = error.ExceptionType,
        Message = error.Message,
        Endpoint = error.Endpoint,
        HttpMethod = error.HttpMethod,
        StatusCode = error.StatusCode,
        StackTrace = error.StackTrace,
        RequestId = error.RequestId,
        TraceId = error.TraceId,
    };

    private static StoreEventResponse ToResponse(Ingestion.Domain.StoreLifecycleEvent @event) => new()
    {
        Id = @event.Id,
        StoreId = @event.StoreId,
        OccurredAt = @event.OccurredAt,
        ReceivedAt = @event.ReceivedAt,
        Type = @event.Type,
        Severity = @event.Severity.ToString(),
        Summary = @event.Summary,
        TraceId = @event.TraceId,
    };

    /// <summary>
    /// Stored JSON is re-parsed on the way out. A store that managed to store
    /// something unparseable gets it dropped rather than breaking the page it
    /// appears on.
    /// </summary>
    private static object? AsDocument(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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

    /// <summary>
    /// <paramref name="customerName"/> is null only where the caller had no
    /// reason to resolve it; the id is always present, so the response stays
    /// usable either way.
    /// </summary>
    internal static StoreResponse ToResponse(Store store, DateTimeOffset now, string? customerName = null) => new()
    {
        Id = store.Id,
        CustomerId = store.CustomerId,
        CustomerName = customerName ?? string.Empty,

        // Installed features are counted by the delivery engine (phase 3.5).
        InstalledFeatureCount = null,
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
