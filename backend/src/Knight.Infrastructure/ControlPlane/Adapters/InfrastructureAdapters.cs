using FeatureDelivery;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Servers;
using Servers.Domain;
using Stores;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// The default infrastructure adapter: it produces nothing and says so. On a real
/// deployment the machine, the agent, the domain and the handshake are produced by
/// an operator, or by the real provider adapter once the hosting platform is
/// chosen — never fabricated by KNIGHT (docs/self-service-saas-plan.md §11).
/// </summary>
internal sealed class ManualInfrastructureAdapter : IInfrastructureAdapter
{
    public bool IsAutomated => false;

    public Task<InfrastructureProgress> EnsureAsync(Guid storeId, CancellationToken cancellationToken) =>
        Task.FromResult(InfrastructureProgress.None);
}

/// <summary>
/// The simulated infrastructure adapter (docs/self-service-saas-plan.md §11). It
/// stands in for a real hosting provider by producing, itself, every fact a
/// provisioning run waits on — a machine with an enrolled agent, a store
/// credential, a verified domain, a completed handshake and a healthy report —
/// and by playing the store's agent to apply the feature-delivery jobs the run
/// queues.
///
/// It is deliberately the same shape a real adapter would be: it goes through the
/// ordinary services (register a server, provision and enrol an agent, issue a
/// credential) rather than reaching past them, so the facts it produces are
/// indistinguishable from the real thing to the fact-based provisioning engine,
/// which is never modified. Every operation is idempotent, because the worker
/// that drives it calls it again on every pass of a run.
/// </summary>
internal sealed class SimulatedInfrastructureAdapter : IInfrastructureAdapter
{
    private readonly ControlPlaneDbContext _context;
    private readonly IServerService _servers;
    private readonly IAgentService _agents;
    private readonly IStoreManagementService _stores;
    private readonly IAgentJobService _jobs;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<SimulatedInfrastructureAdapter> _logger;

    public SimulatedInfrastructureAdapter(
        ControlPlaneDbContext context,
        IServerService servers,
        IAgentService agents,
        IStoreManagementService stores,
        IAgentJobService jobs,
        IDateTimeProvider clock,
        ILogger<SimulatedInfrastructureAdapter> logger)
    {
        _context = context;
        _servers = servers;
        _agents = agents;
        _stores = stores;
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
    }

    public bool IsAutomated => true;

    public async Task<InfrastructureProgress> EnsureAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await _context.Stores
            .Include(item => item.Credentials)
            .FirstOrDefaultAsync(item => item.Id == storeId, cancellationToken);

        if (store is null)
        {
            return InfrastructureProgress.None;
        }

        var now = _clock.UtcNow;

        // 1. A machine, recorded against the store.
        var serverId = store.ServerId ?? await ProvisionServerAsync(store, cancellationToken);

        // 2. An enrolled agent on it.
        var agentReady = await EnsureAgentAsync(serverId, cancellationToken);

        // 3. A usable store credential.
        var credentialReady = store.Credentials.Any(credential => credential.IsUsable(now));
        if (!credentialReady)
        {
            await _stores.IssueCredentialAsync(storeId, cancellationToken);
            credentialReady = true;
        }

        // 4-6. Domain proven, handshake completed, a healthy report — worked
        // directly on the aggregate the way the store itself would drive it, then
        // saved in one unit of work. Reloaded tracked because the credential issue
        // above went through its own service and saved.
        var tracked = await _context.Stores.FirstAsync(item => item.Id == storeId, cancellationToken);

        if (!tracked.IsDomainVerified)
        {
            if (tracked.DomainVerificationToken is null)
            {
                tracked.IssueDomainVerification($"sim-{Guid.NewGuid():n}", now);
            }

            tracked.MarkDomainVerified(DomainVerificationMethod.HttpToken, now);
        }

        var connected = tracked.IntegrationStatus is IntegrationStatus.Connected;
        if (!connected)
        {
            // With the domain proven, a healthy handshake settles the link to
            // Connected — which is exactly the fact the health-check step observes
            // before it lets the store become Active.
            tracked.CompleteHandshake(tracked.Environment, "1.0.0", requireDomainVerification: true, now);
            connected = tracked.IntegrationStatus is IntegrationStatus.Connected;
        }

        await EnsureRuntimeReportedAsync(tracked, now, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        // 7. Play the agent for whatever delivery jobs the run has queued.
        var applied = await ApplyPendingJobsAsync(storeId, cancellationToken);

        _logger.LogInformation(
            "Simulated infrastructure for store {StoreId}: server {ServerId}, agent {Agent}, credential {Cred}, connected {Connected}, {Applied} delivery job(s) applied.",
            storeId,
            serverId,
            agentReady,
            credentialReady,
            connected,
            applied);

        return new InfrastructureProgress(
            ServerReady: true,
            AgentReady: agentReady,
            CredentialReady: credentialReady,
            DomainReady: tracked.IsDomainVerified,
            Connected: connected,
            DeliveryJobsApplied: applied,
            Detail: "Simulated infrastructure produced.");
    }

    private async Task<Guid> ProvisionServerAsync(Store store, CancellationToken cancellationToken)
    {
        var hosting = store.HostingModel is HostingModel.DedicatedManaged
            ? ServerHostingModel.DedicatedManaged
            : ServerHostingModel.SharedManaged;

        var environment = store.Environment switch
        {
            StoreEnvironment.Development => ServerEnvironment.Development,
            StoreEnvironment.Staging => ServerEnvironment.Staging,
            _ => ServerEnvironment.Production,
        };

        var server = await _servers.RegisterAsync(
            new RegisterServerInput(
                $"sim-{store.Slug}",
                hosting,
                environment,
                Provider: "simulated",
                Region: "sim-1",
                IpAddress: null),
            cancellationToken);

        // A dedicated machine must name its customer before a dedicated-hosting
        // store can be placed on it.
        if (hosting is ServerHostingModel.DedicatedManaged)
        {
            await _servers.DedicateAsync(server.Id, store.CustomerId, cancellationToken);
        }

        await _stores.AssignServerAsync(store.Id, server.Id, cancellationToken);
        return server.Id;
    }

    private async Task<bool> EnsureAgentAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var alreadyEnrolled = await _context.Agents
            .AsNoTracking()
            .AnyAsync(
                agent => agent.ServerId == serverId &&
                         (agent.Status == AgentStatus.Online || agent.Status == AgentStatus.Offline),
                cancellationToken);

        if (alreadyEnrolled)
        {
            return true;
        }

        var token = await _servers.ProvisionAgentAsync(serverId, cancellationToken);
        var enrolment = await _agents.EnrolAsync(
            new AgentEnrolmentInput(token.Token, Version: "sim-agent/1.0", Capabilities: null),
            cancellationToken);

        return enrolment is not null;
    }

    /// <summary>
    /// A store cannot be planned against until it has said which runtime it runs
    /// (TODO.md phase 20). A real store reports it in its health checks; the
    /// simulated one records the same so the base-Feature installs can resolve.
    /// </summary>
    private async Task EnsureRuntimeReportedAsync(Store store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hasRuntime = await _context.StoreHealthChecks
            .AsNoTracking()
            .AnyAsync(check => check.StoreId == store.Id, cancellationToken);

        if (hasRuntime)
        {
            return;
        }

        var dependencies = System.Text.Json.JsonSerializer.Serialize(new
        {
            runtime = new Dictionary<string, string>
            {
                ["name"] = "django",
                ["python"] = "3.12.10",
                ["django"] = "5.1.15",
                ["database"] = "postgresql",
            },
        });

        _context.StoreHealthChecks.Add(StoreHealthCheck.Record(
            Guid.NewGuid(),
            store.Id,
            store.CustomerId,
            now,
            StoreHealthStatus.Healthy,
            HealthCheckSource.Heartbeat,
            responseTimeMs: 5,
            reportedVersion: "1.0.0",
            dependencies: dependencies));
    }

    private async Task<int> ApplyPendingJobsAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var applied = 0;

        // Bounded: a store's queue is short, and the guard stops a bug in job
        // completion from becoming an unbounded loop.
        for (var guard = 0; guard < 50; guard++)
        {
            AgentJobAssignment? assignment;
            try
            {
                assignment = await _jobs.ClaimNextAsync(storeId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The simulated agent could not claim a job for store {StoreId}.", storeId);
                break;
            }

            if (assignment is null)
            {
                break;
            }

            foreach (var step in assignment.Steps)
            {
                await _jobs.ReportStepAsync(
                    storeId,
                    assignment.JobId,
                    new StepReport(step, "Succeeded", Output: "simulated", ErrorCode: null, DurationMilliseconds: 1),
                    cancellationToken);
            }

            await _jobs.CompleteAsync(
                storeId,
                assignment.JobId,
                new JobCompletionReport(
                    Succeeded: true,
                    FailureCode: null,
                    FailureMessage: null,
                    RollbackOutcome: null,
                    InstalledVersion: assignment.TargetVersion,
                    Health: "healthy"),
                cancellationToken);

            applied++;
        }

        return applied;
    }
}
