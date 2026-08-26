using FeatureDelivery;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.Ingest;

/// <summary>
/// The job channel a store's agent polls (docs/feature-delivery.md §7).
///
/// Everything here is outbound-only from the store's point of view: the agent
/// asks KNIGHT for work and reports back. KNIGHT never connects inward to run
/// anything, which is what lets a store sit behind a firewall with no inbound
/// port open and still receive features.
///
/// The store identity always comes from the token, never from the request body.
/// An agent that could name its own store id could claim another store's jobs,
/// and a customer with two stores would be one request away from installing into
/// the wrong one.
/// </summary>
public static class StoreJobEndpoints
{
    public static void MapStoreJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/ingest/jobs")
            .RequireAuthorization(StoreAuthorization.Policy)
            .RequireRateLimiting(StoreIngestEndpoints.IngestPolicy)
            .WithTags("Agent jobs");

        // Claiming happens on hand-out rather than in a second call: any gap
        // between "here is your job" and "you now own it" is a window where two
        // agents both believe they hold it.
        group.MapPost("/next", async (
            IStorePrincipal principal,
            IAgentJobService jobs,
            CancellationToken cancellationToken) =>
        {
            var storeId = RequireStore(principal);
            var assignment = await jobs.ClaimNextAsync(storeId, cancellationToken);

            // No work is a perfectly normal answer, and the overwhelmingly common
            // one. 204 keeps it out of the agent's error path and off the
            // dashboard's error rate.
            return assignment is null ? Results.NoContent() : Results.Ok(ToResponse(assignment));
        }).WithSummary("Claims this store's next installation job, if there is one.");

        group.MapPost("/{jobId:guid}/steps", async (
            Guid jobId,
            AgentStepReportRequest request,
            IStorePrincipal principal,
            IAgentJobService jobs,
            CancellationToken cancellationToken) =>
        {
            var storeId = RequireStore(principal);

            await jobs.ReportStepAsync(
                storeId,
                jobId,
                new StepReport(
                    request.Step,
                    request.Status,
                    request.Output,
                    request.ErrorCode,
                    request.DurationMilliseconds),
                cancellationToken);

            return Results.Accepted();
        }).WithSummary("Reports the outcome of one step of a job.");

        group.MapPost("/{jobId:guid}/complete", async (
            Guid jobId,
            AgentJobCompletionRequest request,
            IStorePrincipal principal,
            IAgentJobService jobs,
            CancellationToken cancellationToken) =>
        {
            var storeId = RequireStore(principal);

            await jobs.CompleteAsync(
                storeId,
                jobId,
                new JobCompletionReport(
                    request.Succeeded,
                    request.FailureCode,
                    request.FailureMessage,
                    request.RollbackOutcome,
                    request.InstalledVersion,
                    request.Health),
                cancellationToken);

            return Results.Accepted();
        }).WithSummary("Reports the final outcome of a job.");
    }

    private static AgentJobResponse ToResponse(AgentJobAssignment assignment) => new(
        assignment.JobId,
        assignment.Type,
        assignment.FeatureSlug,
        assignment.TargetVersion,
        assignment.CorrelationId,
        assignment.TraceParent,
        assignment.Steps,
        assignment.NextStep,
        assignment.Artifact is null
            ? null
            : new AgentArtifactResponse(
                assignment.Artifact.PackageReference,
                assignment.Artifact.Digest,
                assignment.Artifact.SizeBytes,
                assignment.Artifact.Signature,
                assignment.Artifact.SigningKeyId,
                assignment.Artifact.DownloadUrl.ToString(),
                assignment.Artifact.DownloadUrlExpiresAt),
        assignment.Configuration is null
            ? null
            : new AgentConfigurationResponse(
                assignment.Configuration.Version,
                assignment.Configuration.ValuesJson,
                assignment.Configuration.Secrets),
        assignment.Migrations is null
            ? null
            : new AgentMigrationResponse(
                assignment.Migrations.Required,
                assignment.Migrations.Reversible,
                assignment.Migrations.RequiresMaintenanceWindow,
                assignment.Migrations.Extensions),
        assignment.Django is null
            ? null
            : new AgentDjangoResponse(
                assignment.Django.AppLabel,
                assignment.Django.InstalledApp,
                assignment.Django.UrlInclude,
                assignment.Django.UrlPrefix,
                [.. assignment.Django.Workers.Select(worker =>
                    new AgentWorkerResponse(worker.Name, worker.Entrypoint, worker.Schedule))]),
        assignment.ClaimExpiresAt);

    /// <summary>
    /// The store's id, from the token. A handler reached without one is a
    /// programming error rather than a request problem: the policy has already
    /// rejected anything that is not a store.
    /// </summary>
    private static Guid RequireStore(IStorePrincipal principal) =>
        principal.StoreId
        ?? throw new InvalidOperationException("A store job endpoint was reached without a store token.");
}
