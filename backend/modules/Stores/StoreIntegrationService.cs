using System.Security.Cryptography;
using System.Text;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Options;
using Stores.Domain;

namespace Stores;

/// <summary>
/// The store side of the link: the handshake, what a store reports afterwards,
/// and proof that it owns the domain KNIGHT will call back on.
///
/// Two properties run through the whole class. A refused handshake says only
/// that it was refused — never which check failed, because that tells an
/// attacker which half of the credential to keep working on. And every check
/// runs before an answer is produced, including the ones that cannot change the
/// outcome once an earlier one has failed, so the time an unknown client id
/// takes to be refused matches the time a wrong secret takes
/// (docs/authentication.md §2).
/// </summary>
internal sealed class StoreIntegrationService : IStoreIntegrationService
{
    /// <summary>
    /// Compared against when no credential matched, so an unknown client id costs
    /// the same hash as a known one. Its value is irrelevant; that it is hashed
    /// is the point.
    /// </summary>
    private const string TimingDecoySecret = "knight-timing-decoy";

    private const int MaxHistoryPageSize = 200;

    private readonly IStoreRepository _stores;
    private readonly IStoreTelemetryRepository _telemetry;
    private readonly ISecureTokenFactory _secrets;
    private readonly IStoreTokenIssuer _tokens;
    private readonly IStorePayloadSigner _signer;
    private readonly IReplayGuard _replay;
    private readonly ICustomerStatusReader _customers;
    private readonly IDomainOwnershipVerifier _domains;
    private readonly IAlertRaiser _alerts;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly StoreOptions _options;

    public StoreIntegrationService(
        IStoreRepository stores,
        IStoreTelemetryRepository telemetry,
        ISecureTokenFactory secrets,
        IStoreTokenIssuer tokens,
        IStorePayloadSigner signer,
        IReplayGuard replay,
        ICustomerStatusReader customers,
        IDomainOwnershipVerifier domains,
        IAlertRaiser alerts,
        IAuditTrail audit,
        IDateTimeProvider clock,
        IOptions<StoreOptions> options)
    {
        _alerts = alerts;
        _stores = stores;
        _telemetry = telemetry;
        _secrets = secrets;
        _tokens = tokens;
        _signer = signer;
        _replay = replay;
        _customers = customers;
        _domains = domains;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<StoreHandshakeResult> HandshakeAsync(StoreHandshakeRequest request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["clientId"] = ["A handshake requires a client id and a client secret."],
            });
        }

        if (!Enum.TryParse<StoreEnvironment>(request.Environment, ignoreCase: true, out var reportedEnvironment))
        {
            // An unrecognised environment is a malformed request, not a refusal:
            // no credential was even considered, so there is nothing to conceal.
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["environment"] = [$"'{request.Environment}' is not a recognised environment."],
            });
        }

        // Replayed handshakes are refused before the credential is looked at. A
        // captured request body is otherwise usable for as long as the secret
        // lives, which is the whole point of a nonce.
        if (!string.IsNullOrWhiteSpace(request.Nonce)
            && !await _replay.TryConsumeAsync("handshake", $"{request.ClientId}:{request.Nonce}", _options.HandshakeNonceWindow, cancellationToken))
        {
            await RecordRefusalAsync(request.ClientId, null, "replayed nonce", cancellationToken);
            return StoreHandshakeResult.Refused(HandshakeRefusal.UnknownCredential);
        }

        var store = await _stores.GetByClientIdAsync(request.ClientId.Trim(), cancellationToken);
        if (store is null)
        {
            // Hash anyway. Returning early here is what makes an unknown client id
            // measurably faster to refuse than a wrong secret.
            _ = _secrets.Hash(TimingDecoySecret);
            await RecordRefusalAsync(request.ClientId, null, "unknown client id", cancellationToken);
            return StoreHandshakeResult.Refused(HandshakeRefusal.UnknownCredential);
        }

        var presentedHash = _secrets.Hash(request.ClientSecret);
        var verification = StoreHandshake.Verify(
            store,
            request.ClientId.Trim(),
            storedHash => FixedTimeEquals(storedHash, presentedHash),
            reportedEnvironment,
            now);

        if (!verification.IsAccepted)
        {
            await RecordRefusalAsync(request.ClientId, store, verification.Refusal.ToString(), cancellationToken);
            return StoreHandshakeResult.Refused(verification.Refusal);
        }

        // Commercial state is checked last and reported as the same refusal: a
        // caller learns that it may not ingest, not why, and not that the client
        // id it guessed happens to exist.
        if (!await _customers.IsOperableAsync(store.CustomerId, cancellationToken))
        {
            await RecordRefusalAsync(request.ClientId, store, "customer not operable", cancellationToken);
            return StoreHandshakeResult.Refused(HandshakeRefusal.StoreNotOperable);
        }

        var credential = verification.Credential!;
        credential.RecordUse(now);

        var outcome = store.CompleteHandshake(
            reportedEnvironment,
            request.StoreVersion,
            _options.RequireDomainVerification,
            now);

        await _telemetry.AddHealthCheckAsync(
            StoreHealthCheck.Record(
                Guid.NewGuid(),
                store.Id,
                store.CustomerId,
                now,
                StoreHealthStatus.Healthy,
                HealthCheckSource.Handshake,
                reportedVersion: request.StoreVersion,
                detail: request.Runtime),
            cancellationToken);

        if (outcome.VersionChanged)
        {
            await _telemetry.AddDeploymentAsync(
                StoreDeployment.Detected(
                    Guid.NewGuid(),
                    store.Id,
                    store.CustomerId,
                    store.ApplicationVersion!,
                    outcome.PreviousVersion,
                    now),
                cancellationToken);
        }

        await _stores.SaveChangesAsync(cancellationToken);
        await _telemetry.SaveChangesAsync(cancellationToken);

        var environmentName = store.Environment.ToString();
        var issued = _tokens.Issue(store.Id, store.CustomerId, environmentName, credential.ClientId);

        await _audit.RecordAsync(
            "store.handshake.accepted",
            nameof(Store),
            store.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new
            {
                clientId = credential.ClientId,
                environment = environmentName,
                version = store.ApplicationVersion,
                integrationStatus = outcome.Status.ToString(),
                domainVerificationOutstanding = outcome.DomainVerificationOutstanding,
            });

        return StoreHandshakeResult.Accepted(new StoreHandshakeAccepted(
            store.Id,
            store.Name,
            store.Slug,
            environmentName,
            outcome.Status,
            issued.Token,
            issued.ExpiresAt,
            (int)issued.Lifetime.TotalSeconds,
            _signer.DeriveVerificationKey(store.Id, environmentName),
            outcome.DomainVerificationOutstanding,

            // Handed back so a store can publish its own proof unattended. It is
            // not a secret — it exists to be published — and it is only ever
            // shown to a caller that already authenticated as this store.
            outcome.DomainVerificationOutstanding ? store.DomainVerificationToken : null,
            (int)_options.HeartbeatInterval.TotalSeconds,
            (int)_options.FeatureRefreshInterval.TotalSeconds));
    }

    public async Task<StoreContactResult> RecordHeartbeatAsync(StoreHeartbeatInput input, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(input.StoreId, cancellationToken);
        var now = _clock.UtcNow;

        var outcome = store.RecordObservation(input.Status, input.StoreVersion, _options.RequireDomainVerification, now);

        await _telemetry.AddHealthCheckAsync(
            StoreHealthCheck.Record(
                Guid.NewGuid(),
                store.Id,
                store.CustomerId,
                now,
                input.Status,
                HealthCheckSource.Heartbeat,
                reportedVersion: input.StoreVersion,
                dependencies: input.DependenciesJson,
                reportedFeatures: input.FeaturesJson,
                detail: input.Detail),
            cancellationToken);

        if (outcome.VersionChanged)
        {
            await _telemetry.AddDeploymentAsync(
                StoreDeployment.Detected(Guid.NewGuid(), store.Id, store.CustomerId, store.ApplicationVersion!, outcome.PreviousVersion, now),
                cancellationToken);
        }

        await _stores.SaveChangesAsync(cancellationToken);
        await _telemetry.SaveChangesAsync(cancellationToken);

        return new StoreContactResult(outcome.Status, outcome.DomainVerificationOutstanding, now);
    }

    public async Task<StoreContactResult> RecordProbeAsync(
        Guid storeId,
        StoreHealthStatus status,
        int? latencyMs,
        string? reportedVersion,
        string? dependenciesJson,
        string? featuresJson,
        string? detail,
        CancellationToken cancellationToken)
    {
        var store = await RequireAsync(storeId, cancellationToken);
        var now = _clock.UtcNow;

        var previousStatus = store.IntegrationStatus;
        var outcome = store.RecordObservation(status, reportedVersion, _options.RequireDomainVerification, now);

        await _telemetry.AddHealthCheckAsync(
            StoreHealthCheck.Record(
                Guid.NewGuid(),
                store.Id,
                store.CustomerId,
                now,
                status,
                HealthCheckSource.Poll,
                latencyMs,
                reportedVersion,
                dependenciesJson,
                featuresJson,
                detail),
            cancellationToken);

        if (outcome.VersionChanged)
        {
            await _telemetry.AddDeploymentAsync(
                StoreDeployment.Detected(Guid.NewGuid(), store.Id, store.CustomerId, store.ApplicationVersion!, outcome.PreviousVersion, now),
                cancellationToken);
        }

        await _stores.SaveChangesAsync(cancellationToken);
        await _telemetry.SaveChangesAsync(cancellationToken);

        // Only a change is audited. A poll that finds everything as it was is
        // evidence, and evidence belongs in the health table, not in the record
        // of things that happened.
        if (previousStatus != outcome.Status)
        {
            await _audit.RecordAsync(
                "store.integration.status_changed",
                nameof(Store),
                store.Id.ToString(),
                store.CustomerId,
                cancellationToken,
                previousValue: new { integrationStatus = previousStatus.ToString() },
                newValue: new { integrationStatus = outcome.Status.ToString(), observed = status.ToString(), detail });
        }

        return new StoreContactResult(outcome.Status, outcome.DomainVerificationOutstanding, now);
    }

    public async Task<StoreDeployment> RecordDeploymentAsync(StoreDeploymentInput input, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(input.StoreId, cancellationToken);
        var now = _clock.UtcNow;

        // A store that reports a deployment has usually already been noticed
        // running the new version — the same handshake that carried the report
        // carried the version. One deployment, so one row: the observation is
        // upgraded rather than duplicated.
        var latest = await _telemetry.GetLatestDeploymentAsync(store.Id, cancellationToken);
        if (latest is { Status: StoreDeploymentStatus.Detected }
            && string.Equals(latest.Version, StoreNormalization.NormalizeVersion(input.Version), StringComparison.Ordinal))
        {
            latest.Confirm(input.Status, input.DeployedAt, input.Notes);
            await _telemetry.SaveChangesAsync(cancellationToken);

            await _audit.RecordAsync(
                "store.deployment.recorded",
                nameof(StoreDeployment),
                latest.Id.ToString(),
                store.CustomerId,
                cancellationToken,
                newValue: new { storeId = store.Id, latest.Version, latest.PreviousVersion, Status = latest.Status.ToString() });

            return latest;
        }

        var deployment = StoreDeployment.Reported(
            Guid.NewGuid(),
            store.Id,
            store.CustomerId,
            input.Version,
            input.PreviousVersion ?? store.ApplicationVersion,
            input.DeployedAt,
            now,
            input.Status,
            input.Notes);

        await _telemetry.AddDeploymentAsync(deployment, cancellationToken);

        // A successful deployment is also the store telling us what it now runs.
        // A failed one is not: the version it reports is the one it tried.
        if (input.Status is StoreDeploymentStatus.Succeeded)
        {
            store.RecordObservation(StoreHealthStatus.Healthy, input.Version, _options.RequireDomainVerification, now);
            await _stores.SaveChangesAsync(cancellationToken);
        }

        await _telemetry.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.deployment.recorded",
            nameof(StoreDeployment),
            deployment.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new { storeId = store.Id, deployment.Version, deployment.PreviousVersion, Status = deployment.Status.ToString() });

        return deployment;
    }

    public async Task<StoreBackup> RecordBackupAsync(StoreBackupInput input, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(input.StoreId, cancellationToken);
        var now = _clock.UtcNow;

        var backup = StoreBackup.Record(
            Guid.NewGuid(),
            store.Id,
            store.CustomerId,
            input.Status,
            input.Kind,
            input.StartedAt,
            input.CompletedAt,
            now,
            input.SizeBytes,
            input.Location,
            input.Detail);

        await _telemetry.AddBackupAsync(backup, cancellationToken);
        await _telemetry.SaveChangesAsync(cancellationToken);

        // Alerting on the report rather than on a sweep, because a failed backup
        // is known the instant it is reported and waiting a quarter of an hour to
        // say so buys nothing. The overdue case — nobody reporting at all — is
        // the one that genuinely needs a timer, and lives with the other rules.
        switch (input.Status)
        {
            case BackupStatus.Failed:
                await _alerts.RaiseAsync(
                    StoreAlertRules.BackupFailed,
                    "Critical",
                    "Store",
                    store.Id,
                    store.CustomerId,
                    $"The {input.Kind.ToString().ToLowerInvariant()} backup of '{store.Name}' failed: " +
                    $"{input.Detail ?? "the store reported no detail."}",
                    cancellationToken);
                break;

            case BackupStatus.Succeeded:
                await _alerts.ResolveAsync(StoreAlertRules.BackupFailed, store.Id, cancellationToken);
                await _alerts.ResolveAsync(StoreAlertRules.BackupOverdue, store.Id, cancellationToken);
                break;
        }

        await _audit.RecordAsync(
            "store.backup.reported",
            nameof(StoreBackup),
            backup.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new
            {
                storeId = store.Id,
                Status = backup.Status.ToString(),
                Kind = backup.Kind.ToString(),
                backup.SizeBytes,
                backup.StartedAt,
                backup.CompletedAt,
            });

        return backup;
    }

    public Task<IReadOnlyCollection<StoreBackup>> ListBackupsAsync(Guid storeId, int limit, CancellationToken cancellationToken) =>
        _telemetry.ListBackupsAsync(storeId, Math.Clamp(limit, 1, MaxHistoryPageSize), cancellationToken);

    public async Task<DomainVerificationChallenge> StartDomainVerificationAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(storeId, cancellationToken);
        var now = _clock.UtcNow;

        var token = $"knight-verify-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
        store.IssueDomainVerification(token, now);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.domain.verification_started",
            nameof(Store),
            store.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new { domain = store.PrimaryDomain });

        return Describe(store)!;
    }

    public async Task<DomainVerificationChallenge?> GetDomainVerificationAsync(Guid storeId, CancellationToken cancellationToken) =>
        Describe(await RequireAsync(storeId, cancellationToken));

    public async Task<DomainVerificationResult> VerifyDomainAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await RequireAsync(storeId, cancellationToken);

        if (store.DomainVerificationToken is not { } token)
        {
            throw new ConflictException("No domain verification has been started for this store.");
        }

        var attempt = await _domains.VerifyAsync(store.PrimaryDomain, token, cancellationToken);
        if (!attempt.Verified)
        {
            await _audit.RecordAsync(
                "store.domain.verification_failed",
                nameof(Store),
                store.Id.ToString(),
                store.CustomerId,
                cancellationToken,
                newValue: new { domain = store.PrimaryDomain, attempt.Method, attempt.Detail });

            return new DomainVerificationResult(false, attempt.Method, attempt.Detail, null);
        }

        var now = _clock.UtcNow;
        var method = Enum.TryParse<DomainVerificationMethod>(attempt.Method, ignoreCase: true, out var parsed)
            ? parsed
            : Domain.DomainVerificationMethod.HttpToken;

        store.MarkDomainVerified(method, now);
        await _stores.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.domain.verified",
            nameof(Store),
            store.Id.ToString(),
            store.CustomerId,
            cancellationToken,
            newValue: new { domain = store.PrimaryDomain, method = method.ToString() });

        return new DomainVerificationResult(true, method.ToString(), attempt.Detail, now);
    }

    public async Task<IReadOnlyCollection<StoreHealthCheck>> ListHealthChecksAsync(Guid storeId, int limit, CancellationToken cancellationToken)
    {
        _ = await RequireAsync(storeId, cancellationToken);
        return await _telemetry.ListHealthChecksAsync(storeId, Clamp(limit), cancellationToken);
    }

    public async Task<IReadOnlyCollection<StoreDeployment>> ListDeploymentsAsync(Guid storeId, int limit, CancellationToken cancellationToken)
    {
        _ = await RequireAsync(storeId, cancellationToken);
        return await _telemetry.ListDeploymentsAsync(storeId, Clamp(limit), cancellationToken);
    }

    private DomainVerificationChallenge? Describe(Store store) =>
        store.DomainVerificationToken is not { } token
            ? null
            : new DomainVerificationChallenge(
                store.Id,
                store.PrimaryDomain,
                token,
                DomainVerificationPaths.HttpPath,
                DomainVerificationPaths.DnsRecordName(store.PrimaryDomain),
                store.DomainVerificationIssuedAt ?? store.CreatedAt,
                store.DomainVerifiedAt);

    private async Task<Store> RequireAsync(Guid storeId, CancellationToken cancellationToken) =>
        await _stores.GetByIdAsync(storeId, cancellationToken)
        ?? throw new NotFoundException($"Store '{storeId}' was not found.");

    /// <summary>
    /// A refusal is audited without the store's customer where none is known: an
    /// unknown client id has no customer to attribute it to, and inventing one
    /// would be worse than a platform-scoped entry.
    /// </summary>
    private Task RecordRefusalAsync(string clientId, Store? store, string reason, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            "store.handshake.refused",
            nameof(Store),
            store?.Id.ToString(),
            store?.CustomerId,
            cancellationToken,
            newValue: new { clientId, reason });

    private static int Clamp(int limit) => limit is < 1 or > MaxHistoryPageSize ? 50 : limit;

    /// <summary>
    /// Both values are hex SHA-256 digests of the same length, so this compares
    /// in constant time without leaking through the length check.
    /// </summary>
    private static bool FixedTimeEquals(string storedHash, string presentedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedHash),
            Encoding.UTF8.GetBytes(presentedHash));
}

/// <summary>
/// Where a domain verification token is published. Shared by the dashboard
/// response, the verifier and the reference store so all three agree without
/// repeating a literal.
/// </summary>
public static class DomainVerificationPaths
{
    public const string HttpPath = "/.well-known/knight-domain-verification";

    public const string DnsPrefix = "_knight-verification";

    public static string DnsRecordName(string domain) => $"{DnsPrefix}.{domain}";
}
