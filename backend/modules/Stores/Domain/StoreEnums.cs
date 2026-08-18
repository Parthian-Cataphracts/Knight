namespace Stores.Domain;

/// <summary>
/// Environments are first class and never inferred: a token minted for one is
/// invalid in another (docs/architecture.md section 8).
/// </summary>
public enum StoreEnvironment
{
    Development = 0,
    Staging = 1,
    Production = 2,
}

/// <summary>
/// Where the store actually runs. Modelled separately from the plan: capability
/// must never be inferred from what the customer pays for
/// (docs/architecture.md section 6).
/// </summary>
public enum HostingModel
{
    SharedManaged = 0,
    DedicatedManaged = 1,
    CustomerManaged = 2,
}

public enum StoreStatus
{
    Provisioning = 0,
    Active = 1,
    Suspended = 2,
    Archived = 3,
}

/// <summary>Technical state of the KNIGHT-to-store link, distinct from the store's own status.</summary>
public enum IntegrationStatus
{
    NotRegistered = 0,
    Pending = 1,
    Connected = 2,
    Degraded = 3,
    Disconnected = 4,
}
