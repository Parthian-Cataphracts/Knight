namespace Tenancy.Domain;

/// <summary>
/// Tracks whether a domain's ownership has been verified. No automated DNS
/// verification exists yet — this is a state a future verification workflow will
/// drive; resolution does not currently gate on it.
/// </summary>
public enum TenantDomainVerificationStatus
{
    Pending = 0,
    Verified = 1,
    Failed = 2
}
