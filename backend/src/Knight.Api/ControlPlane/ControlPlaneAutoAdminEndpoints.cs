using AutoAdmin;
using AutoAdmin.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.ControlPlane;
using Knight.Domain.Exceptions;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The customer's Automatic Admin surface (docs/adr/0038): their autonomy
/// setting and their content runs. Like the rest of the self-service surface,
/// every route resolves the customer from the authenticated principal and never
/// from the request, and the engine gates every action on what the customer is
/// actually entitled to — the API cannot be talked into generating or publishing
/// a part that was not bought.
/// </summary>
public static class ControlPlaneAutoAdminEndpoints
{
    public static void MapControlPlaneAutoAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/me/auto-admin")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Automatic Admin");

        group.MapGet("/settings", async (
            IControlPlanePrincipal principal,
            IAutoAdminService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var settings = await service.GetSettingsAsync(customerId, cancellationToken);
            return Results.Ok(ToSettings(settings));
        });

        group.MapPut("/settings", async (
            SetAutonomyRequest request,
            IControlPlanePrincipal principal,
            IAutoAdminService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            if (!Enum.TryParse<AutonomyMode>(request.Autonomy, ignoreCase: true, out var mode))
            {
                throw DomainException.Validation(
                    $"Unknown autonomy '{request.Autonomy}'. Expected 'ApprovalRequired' or 'FullyAutomatic'.");
            }

            var settings = await service.SetAutonomyAsync(customerId, mode, cancellationToken);
            return Results.Ok(ToSettings(settings));
        });

        // Runs the admin on a topic. Full-auto publishes straight away; otherwise
        // the run comes back as a draft to approve.
        group.MapPost("/runs", async (
            SubmitContentRunRequest request,
            IControlPlanePrincipal principal,
            IAutoAdminService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var job = await service.SubmitAsync(customerId, request.Topic, cancellationToken);
            return Results.Created($"/api/v1/me/auto-admin/runs/{job.Id}", ToRun(job));
        });

        group.MapPost("/runs/{jobId:guid}/approve", async (
            Guid jobId,
            IControlPlanePrincipal principal,
            IAutoAdminService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var job = await service.ApproveAsync(customerId, jobId, cancellationToken);
            return Results.Ok(ToRun(job));
        });

        group.MapGet("/runs", async (
            IControlPlanePrincipal principal,
            IAutoAdminService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var jobs = await service.ListJobsAsync(customerId, cancellationToken);
            return Results.Ok(jobs.Select(ToRun).ToArray());
        });

        group.MapGet("/runs/{jobId:guid}", async (
            Guid jobId,
            IControlPlanePrincipal principal,
            IAutoAdminService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.CustomerId is not { } customerId)
            {
                return Forbidden();
            }

            var job = await service.GetJobAsync(customerId, jobId, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(ToRun(job));
        });
    }

    private static IResult Forbidden() => Results.Problem(
        title: "Only a customer account has an Automatic Admin.",
        statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?> { ["errorCode"] = "UNAUTHORIZED_STORE_ACCESS" });

    private static AutoAdminSettingsResponse ToSettings(AutoAdminSettings settings) => new()
    {
        Autonomy = settings.Autonomy.ToString(),
    };

    private static ContentRunResponse ToRun(ContentJob job) => new()
    {
        Id = job.Id,
        Topic = job.Topic,
        Autonomy = job.Autonomy.ToString(),
        Status = job.Status.ToString(),
        HasPublicationErrors = job.HasPublicationErrors,
        Drafts = job.Drafts
            .Select(draft => new ContentDraftResponse
            {
                Kind = draft.Kind.ToString(),
                Body = draft.Body,
                GeneratorName = draft.GeneratorName,
            })
            .ToArray(),
        Publications = job.Publications
            .Select(publication => new ContentPublicationResponse
            {
                ChannelKey = publication.ChannelKey,
                Succeeded = publication.Succeeded,
                Detail = publication.Detail,
                ExternalReference = publication.ExternalReference,
                PublisherName = publication.PublisherName,
                PublishedAt = publication.PublishedAt,
            })
            .ToArray(),
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt,
    };
}
