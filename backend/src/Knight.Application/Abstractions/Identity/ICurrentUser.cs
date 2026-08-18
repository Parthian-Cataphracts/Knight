namespace Knight.Application.Abstractions.Identity;

public enum PrincipalType
{
    PlatformAdmin,
    TenantUser
}

/// <summary>
/// Read-only view of the authenticated caller for the current request.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    PrincipalType? PrincipalType { get; }

    IReadOnlyCollection<string> Permissions { get; }

    bool HasPermission(string permissionKey);
}
