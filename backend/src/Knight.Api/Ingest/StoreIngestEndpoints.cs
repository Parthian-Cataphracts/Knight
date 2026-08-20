using System.Text.Json;
using Ingestion;
using Knight.Api.ControlPlane;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Contracts.Ingest;
using Microsoft.Extensions.Options;
using Stores;
using Stores.Domain;

namespace Knight.Api.Ingest;

/// <summary>
/// The surface a customer store talks to (docs/api-contracts.md §3).
///
/// Everything here is authenticated by a short-lived store token except the
/// handshake that mints one. Three rules hold across the whole group:
///
/// - The store's identity comes from the token, never from the payload. A body
///   naming a different storeId is not an error to report, it is a field that is
///   never read.
/// - A refusal to authenticate says only that it was refused. Which of the
///   credential, the store or the customer was at fault is a matter for the
///   audit log, not for the caller (docs/authentication.md §2).
/// - Rate limits are per store, not per address: several stores commonly share
///   an egress address, and one of them looping must not silence the others.
/// </summary>
public static class StoreIngestEndpoints
{
    /// <summary>The rate-limit policy protecting the unauthenticated handshake, partitioned by caller address.</summary>
    public const string HandshakePolicy = "ingest-handshake";

    /// <summary>The rate-limit policy protecting every authenticated ingestion call, partitioned by store.</summary>
    public const string IngestPolicy = "ingest";

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static void MapStoreIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/ingest").WithTags("Ingest");

        group.MapPost("/handshake", async (
            StoreHandshakeRequestBody request,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
        {
            var result = await integration.HandshakeAsync(
                new StoreHandshakeRequest(
                    request.ClientId,
                    request.ClientSecret,
                    request.Environment,
                    request.StoreVersion,
                    request.Runtime,
                    request.Nonce),
                cancellationToken);

            if (!result.IsAccepted)
            {
                // One answer for every refusal. A caller that could tell "unknown
                // client id" from "wrong secret" would know which half to keep
                // guessing at.
                return Results.Problem(
                    title: "The credentials were refused.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "unauthorized" });
            }

            var session = result.Session!;

            return Results.Ok(new StoreHandshakeResponse
            {
                StoreId = session.StoreId,
                StoreName = session.StoreName,
                Slug = session.Slug,
                Environment = session.Environment,
                IntegrationStatus = session.IntegrationStatus.ToString(),
                AccessToken = session.AccessToken,
                TokenType = "Bearer",
                ExpiresIn = session.ExpiresInSeconds,
                ExpiresAt = session.ExpiresAt,
                EntitlementSigningKey = session.EntitlementSigningKey,
                DomainVerificationOutstanding = session.DomainVerificationOutstanding,
                DomainVerificationToken = session.DomainVerificationToken,
                DomainVerificationPath = session.DomainVerificationOutstanding ? DomainVerificationPaths.HttpPath : null,
                HeartbeatSeconds = session.HeartbeatSeconds,
                FeatureRefreshSeconds = session.FeatureRefreshSeconds,
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting(HandshakePolicy)
        .WithSummary("Exchanges store credentials for a short-lived store token.");

        var authenticated = group.MapGroup(string.Empty)
            .RequireAuthorization(StoreAuthorization.Policy)
            .RequireRateLimiting(IngestPolicy);

        authenticated.MapPost("/heartbeat", async (
            StoreHeartbeatRequest request,
            IStorePrincipal principal,
            IStoreIntegrationService integration,
            IOptions<StoreOptions> options,
            CancellationToken cancellationToken) =>
        {
            var store = Require(principal);
            RequireEnvironment(store.Environment, request.Environment);

            var status = ParseHealth(request.Status);

            var result = await integration.RecordHeartbeatAsync(
                new StoreHeartbeatInput(
                    store.StoreId,
                    status,
                    request.StoreVersion,
                    Serialise(request.Dependencies),
                    Serialise(request.Features),
                    request.Detail),
                cancellationToken);

            return Results.Ok(new StoreHeartbeatResponse
            {
                IntegrationStatus = result.IntegrationStatus.ToString(),
                DomainVerificationOutstanding = result.DomainVerificationOutstanding,
                ObservedAt = result.ObservedAt,
                HeartbeatSeconds = (int)options.Value.HeartbeatInterval.TotalSeconds,
            });
        })
        .WithSummary("Records a store's own report of its health.");

        authenticated.MapPost("/backups", async (
            StoreBackupReportRequest request,
            IStorePrincipal principal,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
        {
            var store = Require(principal);
            RequireEnvironment(store.Environment, request.Environment);

            if (!Enum.TryParse<BackupStatus>(request.Status, ignoreCase: true, out var status))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"'{request.Status}' is not a recognised backup status."],
                });
            }

            // An unrecognised kind is recorded as scheduled rather than refused.
            // Losing the report — the only evidence that a backup happened at all
            // — over a spelling is the worse of the two failures.
            var kind = Enum.TryParse<BackupKind>(request.Kind, ignoreCase: true, out var parsedKind)
                ? parsedKind
                : BackupKind.Scheduled;

            var backup = await integration.RecordBackupAsync(
                new StoreBackupInput(
                    store.StoreId,
                    status,
                    kind,
                    request.StartedAt,
                    request.CompletedAt,
                    request.SizeBytes,
                    request.Location,
                    request.Detail),
                cancellationToken);

            return Results.Ok(new StoreBackupReportResponse
            {
                BackupId = backup.Id,
                Status = backup.Status.ToString(),
                RecordedAt = backup.ReportedAt,
            });
        })
        .WithSummary("Records a backup a store says it took.");

        authenticated.MapPost("/errors", async (
            ErrorIngestRequest request,
            HttpContext context,
            IStorePrincipal principal,
            IIngestionService ingestion,
            CancellationToken cancellationToken) =>
        {
            var store = Require(principal);

            var receipt = await ingestion.IngestErrorsAsync(
                store,
                request.Environment,
                request.Version,
                request.Events.Select(ToInput).ToArray(),
                IdempotencyKey(context),
                cancellationToken);

            return Accepted(receipt);
        })
        .WithSummary("Accepts a batch of error events.");

        authenticated.MapPost("/events", async (
            EventIngestRequest request,
            HttpContext context,
            IStorePrincipal principal,
            IIngestionService ingestion,
            IStoreIntegrationService integration,
            CancellationToken cancellationToken) =>
        {
            var store = Require(principal);

            var receipt = await ingestion.IngestEventsAsync(
                store,
                request.Environment,
                request.Events.Select(ToInput).ToArray(),
                IdempotencyKey(context),
                cancellationToken);

            // Deployment reports are the one event type KNIGHT acts on rather
            // than merely records: they are how a store tells us what it is
            // running, which is the fact the whole delivery phase depends on.
            if (!receipt.Duplicate)
            {
                await RecordDeploymentsAsync(store, request.Events, integration, cancellationToken);
            }

            return Accepted(receipt);
        })
        .WithSummary("Accepts a batch of store lifecycle events.");

        authenticated.MapPost("/logs", async (
            LogIngestRequest request,
            HttpContext context,
            IStorePrincipal principal,
            IIngestionService ingestion,
            CancellationToken cancellationToken) =>
        {
            var store = Require(principal);

            var receipt = await ingestion.IngestLogsAsync(
                store,
                request.Environment,
                request.Version,
                request.Entries.Select(ToInput).ToArray(),
                IdempotencyKey(context),
                cancellationToken);

            return Accepted(receipt);
        })
        .WithSummary("Accepts a batch of structured log entries. Requires the log-shipping entitlement.");

        authenticated.MapGet("/features", async (
            IStorePrincipal principal,
            ICustomerEntitlementReader entitlements,
            IStorePayloadSigner signer,
            IDateTimeProvider clock,
            IOptions<StoreOptions> options,
            CancellationToken cancellationToken) =>
        {
            var store = Require(principal);
            var held = await entitlements.ListActiveAsync(store.CustomerId, cancellationToken);

            var issuedAt = clock.UtcNow;
            var staleAfter = issuedAt + options.Value.FeatureRefreshInterval + options.Value.FeatureRefreshInterval;

            var payload = new EntitlementSetResponse
            {
                StoreId = store.StoreId,
                CustomerId = store.CustomerId,
                Environment = store.Environment,
                IssuedAt = issuedAt,
                StaleAfter = staleAfter,
                Features = held.Select(feature => new EntitledFeatureResponse
                {
                    FeatureId = feature.FeatureId,
                    Slug = feature.Slug,
                    Name = feature.Name,
                    Source = feature.Source,
                    GrantedAt = feature.GrantedAt,
                    ExpiresAt = feature.ExpiresAt,
                }).ToArray(),
                Signature = string.Empty,
                SignatureVersion = EntitlementSignature.Version,
            };

            return Results.Ok(payload with
            {
                Signature = signer.Sign(store.StoreId, store.Environment, EntitlementSignature.Canonicalise(payload)),
            });
        })
        .WithSummary("The effective entitlement set for this store, signed so it can be cached and trusted offline.");
    }

    /// <summary>
    /// The store's identity, taken from the token. A handler that needs it and
    /// cannot get it is a programming error, not a request problem: the policy
    /// has already rejected anything without a store token.
    /// </summary>
    private static IngestingStore Require(IStorePrincipal principal) =>
        principal is { StoreId: { } storeId, CustomerId: { } customerId, Environment: { } environment }
            ? new IngestingStore(storeId, customerId, environment)
            : throw new InvalidOperationException("A store endpoint was reached without a store token.");

    private static void RequireEnvironment(string tokenEnvironment, string payloadEnvironment)
    {
        if (!string.Equals(tokenEnvironment, payloadEnvironment?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new Knight.Application.Exceptions.ValidationException(new Dictionary<string, string[]>
            {
                ["environment"] = [$"This store is registered as '{tokenEnvironment}'."],
            });
        }
    }

    private static StoreHealthStatus ParseHealth(string status) => status?.Trim().ToLowerInvariant() switch
    {
        "healthy" or "ok" or "up" => StoreHealthStatus.Healthy,
        "degraded" or "warn" or "warning" => StoreHealthStatus.Degraded,
        _ => StoreHealthStatus.Unhealthy,
    };

    private static async Task RecordDeploymentsAsync(
        IngestingStore store,
        IEnumerable<StoreEventBody> events,
        IStoreIntegrationService integration,
        CancellationToken cancellationToken)
    {
        foreach (var body in events)
        {
            var type = body.Type?.Trim().ToLowerInvariant();

            var status = type switch
            {
                Ingestion.Domain.StoreLifecycleEvent.DeploymentCompletedType => StoreDeploymentStatus.Succeeded,
                Ingestion.Domain.StoreLifecycleEvent.DeploymentFailedType => StoreDeploymentStatus.Failed,
                _ => (StoreDeploymentStatus?)null,
            };

            if (status is not { } deploymentStatus)
            {
                continue;
            }

            // A deployment event without a version says nothing usable, so it
            // stays an event and does not become a deployment record.
            if (ReadString(body.Payload, "version") is not { } version)
            {
                continue;
            }

            await integration.RecordDeploymentAsync(
                new StoreDeploymentInput(
                    store.StoreId,
                    version,
                    ReadString(body.Payload, "previousVersion"),
                    body.OccurredAt,
                    deploymentStatus,
                    body.Summary),
                cancellationToken);
        }
    }

    private static string? ReadString(Dictionary<string, object>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var text = value is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : value.ToString();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static ErrorEventInput ToInput(ErrorEventBody body) => new(
        body.OccurredAt,
        body.ExceptionType,
        body.Message,
        body.Endpoint,
        body.HttpMethod,
        body.StatusCode,
        body.StackTrace,
        body.RequestId,
        body.TraceId,
        Serialise(body.Context));

    private static LifecycleEventInput ToInput(StoreEventBody body) => new(
        body.OccurredAt,
        body.Type,
        body.Severity,
        body.Summary,
        body.TraceId,
        Serialise(body.Payload));

    private static LogEntryInput ToInput(LogEntryBody body) => new(
        body.Timestamp,
        body.Level,
        body.Service,
        body.Message,
        body.RequestId,
        body.TraceId,
        body.Exception,
        Serialise(body.Attributes));

    private static string? Serialise(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, PayloadOptions);

    /// <summary>
    /// A batch may name itself so a retry after a timeout does not double-count.
    /// The header is the standard one; absent, the batch is simply written
    /// (docs/api-contracts.md §1).
    /// </summary>
    private static string? IdempotencyKey(HttpContext context) =>
        context.Request.Headers.TryGetValue("Idempotency-Key", out var values) ? values.ToString() : null;

    private static IResult Accepted(IngestionReceipt receipt) =>
        Results.Accepted(value: new IngestReceiptResponse
        {
            Accepted = receipt.Accepted,
            Rejected = receipt.Rejected,
            Duplicate = receipt.Duplicate,
            Errors = receipt.Errors.ToArray(),
        });
}
