using Knight.Domain.Exceptions;

namespace FeatureRegistry.Domain;

/// <summary>
/// A feature version, parsed rather than compared as a string.
///
/// Every ordering question in this subsystem — is this an upgrade or a
/// downgrade, does the store satisfy the manifest's constraint, which of two
/// candidates is newer — is a version comparison, and string comparison gets
/// every one of them wrong the moment a component reaches ten. So versions are
/// parsed once, at the edge, and compared as numbers thereafter.
///
/// The grammar is the subset of semver 2.0.0 the registry actually needs:
/// <c>major.minor.patch</c> with an optional pre-release. Build metadata is
/// accepted and discarded, exactly as the specification requires — it is not
/// part of identity, so <c>1.0.0+a</c> and <c>1.0.0+b</c> are the same release
/// and publishing both must collide rather than produce two artifacts.
/// </summary>
public sealed record SemanticVersion : IComparable<SemanticVersion>
{
    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Null for a stable release; the dot-separated identifiers otherwise.</summary>
    public string? PreRelease { get; }

    /// <summary>True when this version is a pre-release and so not a candidate for an ordinary upgrade.</summary>
    public bool IsPreRelease => PreRelease is not null;

    private SemanticVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw DomainException.Validation($"'{value}' is not a valid semantic version.");

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default!;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // Build metadata is stripped before anything else: it may legally
        // contain the same characters as a pre-release, so leaving it in place
        // would make "1.0.0+beta.1" parse as a pre-release it is not.
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            if (plus == text.Length - 1)
            {
                return false;
            }

            text = text[..plus];
        }

        string? preRelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];

            if (!IsValidPreRelease(preRelease))
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            // Leading zeroes are rejected rather than tolerated: "1.01.0" and
            // "1.1.0" would otherwise be two spellings of one version, and the
            // registry's uniqueness constraint is on the spelling.
            if (parts[i].Length == 0 ||
                (parts[i].Length > 1 && parts[i][0] == '0') ||
                !int.TryParse(parts[i], out numbers[i]) ||
                numbers[i] < 0)
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    private static bool IsValidPreRelease(string preRelease)
    {
        if (preRelease.Length == 0)
        {
            return false;
        }

        foreach (var identifier in preRelease.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            foreach (var character in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var numeric = Major.CompareTo(other.Major);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0)
        {
            return numeric;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Pre-release ordering per semver §11.4. The rule that matters most here is
    /// the first one: a version with a pre-release is *lower* than the same
    /// version without, so 1.4.0-rc.1 never satisfies a constraint that 1.4.0
    /// would be the top of.
    /// </summary>
    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');

        for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            // A larger set of identifiers wins when all the preceding ones are
            // equal: rc.1 precedes rc.1.1.
            if (i >= leftParts.Length)
            {
                return -1;
            }

            if (i >= rightParts.Length)
            {
                return 1;
            }

            var leftNumeric = int.TryParse(leftParts[i], out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[i], out var rightNumber);

            var comparison = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),

                // Numeric identifiers always have lower precedence than
                // alphanumeric ones, so rc.2 precedes rc.beta.
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[i], rightParts[i]),
            };

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
