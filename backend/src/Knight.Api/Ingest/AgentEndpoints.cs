using Knight.Contracts.ControlPlane;
using Servers;

namespace Knight.Api.Ingest;

/// <summary>
/// The agent's own channel: enrol and heartbeat.
///
/// Deliberately separate from the feature-delivery job channel, and deliberately
/// smaller. An agent authenticating here can say how its machine is; it cannot
/// ask for work and nothing here accepts a command. Being told to do something is
/// the job channel's business, and that vocabulary is closed
/// (docs/feature-delivery.md §15).
///
/// Authentication is a credential presented on each call rather than a session,
/// because an agent is a daemon on somebody else's infrastructure: giving it a
/// token to keep would be giving it something to leak, and the credential it
/// already holds is the thing under the operator's control via revocation.
/// </summary>
public static class AgentEndpoints
{
    /// <summary>Header carrying the agent's id. Paired with the credential below.</summary>
    private const string AgentIdHeader = "X-Knight-Agent-Id";

    /// <summary>Header carrying the agent's credential.</summary>
    private const string AgentCredentialHeader = "X-Knight-Agent-Credential";

    public static void MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/agent")
            .RequireRateLimiting(StoreIngestEndpoints.IngestPolicy)
            .WithTags("Agent");

        // Enrolment is anonymous by necessity: the agent has no credential yet.
        // The one-time provisioning token is what authenticates it, and it is
        // burned on success.
        group.MapPost("/enrol", async (
            AgentEnrolRequest request,
            IAgentService agents,
            CancellationToken cancellationToken) =>
        {
            var result = await agents.EnrolAsync(
                new AgentEnrolmentInput(request.ProvisioningToken, request.Version, request.Capabilities),
                cancellationToken);

            if (result is null)
            {
                // One answer for unknown, expired and already-used. Letting a
                // caller tell those apart would make this an oracle for guessing
                // provisioning tokens.
                return Results.Problem(
                    title: "The provisioning token was refused.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "unauthorized" });
            }

            return Results.Ok(new AgentEnrolResponse(
                result.AgentId,
                result.ServerId,
                result.Credential,
                (int)result.HeartbeatInterval.TotalSeconds));
        })
        .AllowAnonymous()
        .WithSummary("Exchanges a one-time provisioning token for a lasting agent credential.");

        group.MapPost("/heartbeat", async (
            AgentHeartbeatRequest request,
            HttpContext http,
            IAgentService agents,
            CancellationToken cancellationToken) =>
        {
            var agent = await AuthenticateAsync(http, agents, cancellationToken);
            if (agent is null)
            {
                return Unauthorized();
            }

            var result = await agents.HeartbeatAsync(
                agent.Id,
                new AgentHeartbeatInput(
                    request.Version,
                    request.Capabilities,
                    request.Metrics is null
                        ? null
                        : new ServerMetricSample(
                            request.Metrics.CpuPercent,
                            request.Metrics.MemoryUsedBytes,
                            request.Metrics.MemoryTotalBytes,
                            request.Metrics.DiskUsedBytes,
                            request.Metrics.DiskTotalBytes,
                            request.Metrics.NetInBytes,
                            request.Metrics.NetOutBytes,
                            request.Metrics.LoadAverage)),
                cancellationToken);

            return Results.Ok(new AgentHeartbeatResponse(
                (int)result.HeartbeatInterval.TotalSeconds,
                result.ServerStatus.ToString()));
        })
        .AllowAnonymous()
        .WithSummary("Reports that the agent is alive, and optionally a metric sample.");
    }

    /// <summary>
    /// Authenticates the agent from its headers, or returns null.
    ///
    /// Anonymous at the routing layer and authenticated here, because an agent
    /// credential is not a bearer token the platform's JWT pipeline understands —
    /// it is an opaque secret compared against a stored hash, which is what lets
    /// revocation take effect immediately rather than when a token happens to
    /// expire.
    /// </summary>
    private static async Task<Servers.Domain.Agent?> AuthenticateAsync(
        HttpContext http,
        IAgentService agents,
        CancellationToken cancellationToken)
    {
        if (!http.Request.Headers.TryGetValue(AgentIdHeader, out var idValues) ||
            !http.Request.Headers.TryGetValue(AgentCredentialHeader, out var credentialValues) ||
            !Guid.TryParse(idValues.ToString(), out var agentId))
        {
            return null;
        }

        return await agents.AuthenticateAsync(agentId, credentialValues.ToString(), cancellationToken);
    }

    private static IResult Unauthorized() =>
        Results.Problem(
            title: "The agent credentials were refused.",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?> { ["errorCode"] = "unauthorized" });
}
