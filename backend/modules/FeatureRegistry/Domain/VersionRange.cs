using Knight.Domain.Exceptions;

namespace FeatureRegistry.Domain;

/// <summary>
/// A constraint on a version, as written in a manifest: <c>"&gt;=4.0.0,&lt;6.0.0"</c>.
///
/// The grammar is a comma-separated set of comparators, all of which must hold.
/// There is deliberately no <c>||</c>: a dependency satisfied by two disjoint
/// ranges is a dependency nobody can reason about at three in the morning, and
/// every case the manifests actually need is a single interval
/// (docs/adr/0017-feature-compatibility-and-dependencies.md).
///
/// Two conveniences exist because the manifest speaks about more than features.
/// Operands may be partial — <c>"&gt;=5.0"</c> and <c>"&gt;=3.12"</c> are how
/// Django and Python versions are written in the wild — and are padded with
/// zeroes. And a bare version with no operator means exactly that version, so
/// <c>"1.4.0"</c> pins rather than silently meaning "at least".
/// </summary>
public sealed record VersionRange
{
    private readonly IReadOnlyList<Comparator> _comparators;

    /// <summary>The range exactly as written, so an error can quote the author back to themselves.</summary>
    public string Expression { get; }

    /// <summary>True when the range names no constraint at all.</summary>
    public bool IsUnbounded => _comparators.Count == 0;

    private VersionRange(string expression, IReadOnlyList<Comparator> comparators)
    {
        Expression = expression;
        _comparators = comparators;
    }

    /// <summary>The range that admits everything, used where a manifest omits a constraint entirely.</summary>
    public static VersionRange Any { get; } = new("*", []);

    public static VersionRange Parse(string? value) =>
        TryParse(value, out var range)
            ? range
            : throw DomainException.Validation($"'{value}' is not a valid version range.");

    public static bool TryParse(string? value, out VersionRange range)
    {
        range = default!;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var expression = value.Trim();
        if (expression is "*")
        {
            range = Any;
            return true;
        }

        var comparators = new List<Comparator>();
        // Empty terms are refused rather than skipped: a trailing or doubled
        // comma almost always means a comparator was deleted and the range now
        // admits more than its author thinks it does.
        foreach (var term in expression.Split(',', StringSplitOptions.TrimEntries))
        {
            if (term.Length == 0 || !TryParseComparator(term, out var comparator))
            {
                return false;
            }

            comparators.Add(comparator);
        }

        if (comparators.Count == 0)
        {
            return false;
        }

        range = new VersionRange(expression, comparators);
        return true;
    }

    private static bool TryParseComparator(string term, out Comparator comparator)
    {
        comparator = default;

        // Longest operators first: "<=" must not be read as "<" followed by a
        // version that happens to start with "=".
        var (op, operandText) = term switch
        {
            _ when term.StartsWith(">=", StringComparison.Ordinal) => (ComparisonOperator.GreaterOrEqual, term[2..]),
            _ when term.StartsWith("<=", StringComparison.Ordinal) => (ComparisonOperator.LessOrEqual, term[2..]),
            _ when term.StartsWith("==", StringComparison.Ordinal) => (ComparisonOperator.Equal, term[2..]),
            _ when term.StartsWith("!=", StringComparison.Ordinal) => (ComparisonOperator.NotEqual, term[2..]),
            _ when term.StartsWith(">", StringComparison.Ordinal) => (ComparisonOperator.Greater, term[1..]),
            _ when term.StartsWith("<", StringComparison.Ordinal) => (ComparisonOperator.Less, term[1..]),
            _ when term.StartsWith("=", StringComparison.Ordinal) => (ComparisonOperator.Equal, term[1..]),
            _ => (ComparisonOperator.Equal, term),
        };

        if (!TryParseOperand(operandText.Trim(), out var operand))
        {
            return false;
        }

        comparator = new Comparator(op, operand);
        return true;
    }

    /// <summary>
    /// Parses an operand, padding a partial version with zeroes. Padding is safe
    /// in both directions because it lands on the boundary the author means:
    /// "&lt;6.0" becomes "&lt;6.0.0", which excludes every 6.x, and "&gt;=5.0"
    /// becomes "&gt;=5.0.0", which admits every 5.x.
    /// </summary>
    private static bool TryParseOperand(string text, out SemanticVersion operand)
    {
        operand = default!;

        if (text.Length == 0)
        {
            return false;
        }

        var core = text;
        var suffix = string.Empty;

        var marker = core.IndexOfAny(['-', '+']);
        if (marker >= 0)
        {
            suffix = core[marker..];
            core = core[..marker];
        }

        var componentCount = core.Split('.').Length;
        if (componentCount is < 1 or > 3)
        {
            return false;
        }

        var padded = core + string.Concat(Enumerable.Repeat(".0", 3 - componentCount)) + suffix;
        return SemanticVersion.TryParse(padded, out operand);
    }

    /// <summary>
    /// Whether a version satisfies every comparator in the range.
    ///
    /// A pre-release satisfies a range only when the range itself mentions a
    /// pre-release of the same major.minor.patch. Without that rule a store on
    /// "&gt;=1.0.0" would be handed 2.0.0-rc.1 the moment somebody published a
    /// release candidate, which is never what the author meant.
    /// </summary>
    public bool Includes(SemanticVersion version)
    {
        if (IsUnbounded)
        {
            return !version.IsPreRelease;
        }

        if (version.IsPreRelease && !AllowsPreReleaseOf(version))
        {
            return false;
        }

        foreach (var comparator in _comparators)
        {
            if (!comparator.IsSatisfiedBy(version))
            {
                return false;
            }
        }

        return true;
    }

    private bool AllowsPreReleaseOf(SemanticVersion version)
    {
        foreach (var comparator in _comparators)
        {
            var operand = comparator.Operand;
            if (operand.IsPreRelease &&
                operand.Major == version.Major &&
                operand.Minor == version.Minor &&
                operand.Patch == version.Patch)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The highest candidate that satisfies the range, or null when none does.
    /// Resolution always takes the highest match rather than the first: a store
    /// should receive the newest release its constraints allow.
    /// </summary>
    public SemanticVersion? BestMatch(IEnumerable<SemanticVersion> candidates)
    {
        SemanticVersion? best = null;

        foreach (var candidate in candidates)
        {
            if (Includes(candidate) && (best is null || candidate > best))
            {
                best = candidate;
            }
        }

        return best;
    }

    public override string ToString() => Expression;

    private enum ComparisonOperator
    {
        Equal,
        NotEqual,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
    }

    private readonly record struct Comparator(ComparisonOperator Operator, SemanticVersion Operand)
    {
        public bool IsSatisfiedBy(SemanticVersion version) => Operator switch
        {
            ComparisonOperator.Equal => version.CompareTo(Operand) == 0,
            ComparisonOperator.NotEqual => version.CompareTo(Operand) != 0,
            ComparisonOperator.Greater => version > Operand,
            ComparisonOperator.GreaterOrEqual => version >= Operand,
            ComparisonOperator.Less => version < Operand,
            ComparisonOperator.LessOrEqual => version <= Operand,
            _ => false,
        };
    }
}
