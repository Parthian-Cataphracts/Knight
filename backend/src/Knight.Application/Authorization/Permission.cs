using System.Text.RegularExpressions;

namespace Knight.Application.Authorization;

/// <summary>
/// A stable, machine-readable permission identifier in "area.resource.action" form,
/// e.g. "catalog.products.create", with optional descriptive metadata for admin
/// tooling. The <see cref="Key"/> alone is the security identifier — equality,
/// hashing, and catalog membership never consider <see cref="Description"/> or
/// <see cref="Module"/>. Modules declare their own permissions; this type only
/// enforces the shared naming convention.
/// </summary>
public readonly partial struct Permission : IEquatable<Permission>
{
    public string Key { get; }

    public string? Description { get; }

    public string? Module { get; }

    public Permission(string key, string? description = null, string? module = null)
    {
        if (string.IsNullOrWhiteSpace(key) || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                "Permission keys must be lowercase, dot-separated segments (e.g. 'catalog.products.view').",
                nameof(key));
        }

        Key = key;
        Description = description;
        Module = module;
    }

    public bool Equals(Permission other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Permission other && Equals(other);

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Key;

    public static implicit operator string(Permission permission) => permission.Key;

    [GeneratedRegex("^[a-z0-9]+(\\.[a-z0-9-]+)+$")]
    private static partial Regex KeyPattern();
}
