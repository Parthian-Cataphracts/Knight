namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// The three principal types KNIGHT authenticates. They never share credentials,
/// tokens or policies, and cross-type access is rejected at the policy layer
/// before any handler runs (docs/authentication.md §4).
/// </summary>
public static class PrincipalTypes
{
    public const string ClaimType = "principal_type";

    public const string User = "user";

    public const string Store = "store";

    public const string Agent = "agent";
}

/// <summary>Claim names used by control-plane access tokens.</summary>
public static class ControlPlaneClaims
{
    public const string CustomerId = "customer_id";

    public const string SessionId = "session_id";

    public const string Environment = "env";

    public const string Role = "roles";

    public const string MfaSatisfied = "amr";
}
