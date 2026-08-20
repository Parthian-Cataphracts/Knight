using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Servers.Domain;

/// <summary>
/// A machine KNIGHT knows about (docs/domain-model.md §7).
///
/// A server is infrastructure, not commerce. It may host one customer's stores or
/// several, and it belongs to the platform rather than to a customer — which is
/// why it carries no customer id and is not customer-scoped. A customer sees the
/// health of *their store*, never the machine it shares with somebody else's.
///
/// The status here is derived, never set by hand. It is what the last heartbeat
/// and the evaluation rules say it is, so that "the dashboard said healthy" and
/// "nothing has reported in an hour" cannot both be true.
/// </summary>
public sealed class Server : AuditableEntity
{
    public string Name { get; private set; }

    /// <summary>Whether the machine is shared, dedicated to one customer, or the customer's own.</summary>
    public ServerHostingModel HostingModel { get; private set; }

    public string? Provider { get; private set; }

    public string? Region { get; private set; }

    /// <summary>
    /// Stored for operators to find the box. Never used to decide anything: an
    /// address is not an identity, and an agent proves who it is with a token.
    /// </summary>
    public string? IpAddress { get; private set; }

    public ServerEnvironment Environment { get; private set; }

    /// <summary>
    /// The customer a dedicated machine belongs to. Null for shared hardware,
    /// and required for a dedicated one — a machine sold as dedicated with no
    /// customer recorded is a machine nobody can prove is dedicated to anybody
    /// (docs/store-provisioning.md §2).
    /// </summary>
    public Guid? DedicatedCustomerId { get; private set; }

    public ServerStatus Status { get; private set; }

    /// <summary>When an agent on this server last checked in. Null until the first one does.</summary>
    public DateTimeOffset? LastSeenAt { get; private set; }

    /// <summary>
    /// Why the server is in its current status, in words. An operator opening a
    /// red row should not have to infer what went wrong from a timestamp.
    /// </summary>
    public string? StatusReason { get; private set; }

    public DateTimeOffset? DecommissionedAt { get; private set; }

    private Server()
    {
        Name = string.Empty;
    }

    private Server(
        Guid id,
        DateTimeOffset createdAt,
        string name,
        ServerHostingModel hostingModel,
        ServerEnvironment environment,
        string? provider,
        string? region,
        string? ipAddress)
        : base(id, createdAt)
    {
        Name = name;
        HostingModel = hostingModel;
        Environment = environment;
        Provider = provider;
        Region = region;
        IpAddress = ipAddress;

        // A server that has never been heard from is Unknown, not Healthy.
        // Optimism here would mean a box that never came up looking fine.
        Status = ServerStatus.Unknown;
    }

    public static Server Register(
        Guid id,
        DateTimeOffset createdAt,
        string name,
        ServerHostingModel hostingModel,
        ServerEnvironment environment,
        string? provider = null,
        string? region = null,
        string? ipAddress = null)
        => new(
            id,
            createdAt,
            RequireName(name),
            hostingModel,
            environment,
            Trim(provider, 100),
            Trim(region, 100),
            Trim(ipAddress, 45));

    /// <summary>
    /// Records which customer a dedicated machine is for, or clears it when the
    /// machine goes back into the shared pool.
    ///
    /// The dedication is enforced where stores are assigned: a store may only be
    /// placed on a dedicated machine belonging to its own customer. Recording it
    /// here is what makes that check possible at all.
    /// </summary>
    public void DedicateTo(Guid? customerId, DateTimeOffset now)
    {
        EnsureActive();

        if (customerId is null && HostingModel is ServerHostingModel.DedicatedManaged)
        {
            throw DomainException.Conflict("A dedicated server must name the customer it is dedicated to.");
        }

        if (customerId is not null && HostingModel is ServerHostingModel.SharedManaged)
        {
            throw DomainException.Conflict("A shared server hosts several customers and cannot be dedicated to one.");
        }

        DedicatedCustomerId = customerId;
        MarkUpdated(now);
    }

    public void UpdateDetails(string name, string? provider, string? region, string? ipAddress, DateTimeOffset now)
    {
        EnsureActive();

        Name = RequireName(name);
        Provider = Trim(provider, 100);
        Region = Trim(region, 100);
        IpAddress = Trim(ipAddress, 45);
        MarkUpdated(now);
    }

    /// <summary>
    /// Records that an agent checked in, and returns the machine to health.
    ///
    /// A heartbeat is the only thing that can clear an Offline status. Nothing
    /// else knows the box is back, and letting an operator mark it healthy by
    /// hand would let the dashboard disagree with reality.
    /// </summary>
    public void RecordHeartbeat(DateTimeOffset now)
    {
        EnsureActive();

        LastSeenAt = now;
        Status = ServerStatus.Healthy;
        StatusReason = null;
        MarkUpdated(now);
    }

    /// <summary>
    /// Applies an evaluated status. Called by the rules, never by a request
    /// handler.
    /// </summary>
    public void ApplyStatus(ServerStatus status, string? reason, DateTimeOffset now)
    {
        EnsureActive();

        if (Status == status && string.Equals(StatusReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        Status = status;
        StatusReason = Trim(reason, 500);
        MarkUpdated(now);
    }

    /// <summary>
    /// Whether the server has gone quiet.
    ///
    /// Three missed intervals rather than one: a single missed heartbeat is a
    /// network hiccup, and paging somebody for it is how alerts get ignored
    /// (docs/observability.md §8).
    /// </summary>
    public bool IsOverdue(DateTimeOffset now, TimeSpan heartbeatInterval, int missedIntervals = 3)
    {
        if (LastSeenAt is null)
        {
            return false;
        }

        return now - LastSeenAt.Value > heartbeatInterval * missedIntervals;
    }

    /// <summary>
    /// Takes the machine out of service. Kept rather than deleted: its metrics
    /// and the alerts it raised are part of the record of what happened.
    /// </summary>
    public void Decommission(DateTimeOffset now)
    {
        if (DecommissionedAt is not null)
        {
            throw DomainException.Conflict("The server is already decommissioned.");
        }

        DecommissionedAt = now;
        Status = ServerStatus.Offline;
        StatusReason = "Decommissioned.";
        MarkUpdated(now);
    }

    public bool IsActive => DecommissionedAt is null;

    private void EnsureActive()
    {
        if (DecommissionedAt is not null)
        {
            throw DomainException.Conflict("A decommissioned server cannot be changed.");
        }
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("A server must have a name.");
        }

        var trimmed = name.Trim();
        return trimmed.Length <= 200
            ? trimmed
            : throw DomainException.Validation("A server name must be 200 characters or fewer.");
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw DomainException.Validation($"The value '{trimmed[..20]}…' is too long.");
    }
}

/// <summary>Where the machine sits commercially. Mirrors the store's hosting model deliberately: the two must agree.</summary>
public enum ServerHostingModel
{
    SharedManaged = 0,
    DedicatedManaged = 1,
    CustomerManaged = 2,
}

public enum ServerEnvironment
{
    Development = 0,
    Staging = 1,
    Production = 2,
}

public enum ServerStatus
{
    /// <summary>Registered, never heard from. Not the same as healthy.</summary>
    Unknown = 0,

    Healthy = 1,

    /// <summary>Reporting, but something it reported is out of bounds.</summary>
    Degraded = 2,

    /// <summary>Has not reported for long enough that it is presumed down.</summary>
    Offline = 3,
}
