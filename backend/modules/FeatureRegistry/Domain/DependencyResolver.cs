namespace FeatureRegistry.Domain;

/// <summary>
/// Turns "install this Feature into this store" into an ordered, checked plan —
/// or into the list of reasons there is no plan
/// (docs/feature-delivery.md §8, docs/adr/0017-feature-compatibility-and-dependencies.md).
///
/// The algorithm is a constraint fixpoint rather than a depth-first walk, because
/// a depth-first walk gets diamonds wrong. When both A and B depend on Core, the
/// version Core resolves to has to satisfy *both* of their ranges, and a walk
/// that picks a version the first time it meets Core has already committed
/// before it has seen the second constraint. So constraints are accumulated per
/// slug, the highest version satisfying all of them is chosen, and choosing it
/// can introduce further constraints — which is why it repeats until nothing
/// changes.
///
/// Termination is not left to faith. Every iteration either adds a constraint or
/// lowers a chosen version, both of which are bounded, and the loop additionally
/// refuses to run more times than there are features in the registry. A resolver
/// that can hang is a resolver that can hang a store's install job.
///
/// The resolver is a pure function: same registry, same store, same request,
/// same answer. Nothing here reads a clock, a database or a configuration file.
/// </summary>
public sealed class DependencyResolver
{
    private readonly IReadOnlyDictionary<string, RegistryFeature> _features;

    public DependencyResolver(IEnumerable<RegistryFeature> features)
    {
        _features = features.ToDictionary(feature => feature.Slug, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves a single root request.
    /// </summary>
    /// <param name="rootSlug">The Feature the caller actually asked for.</param>
    /// <param name="rootRange">
    /// The permitted versions. Usually the plan's pinned range; <see cref="VersionRange.Any"/>
    /// means "whatever is newest".
    /// </param>
    /// <param name="store">What the target store is running.</param>
    public ResolutionResult Resolve(string rootSlug, VersionRange rootRange, StoreCompatibilityContext store)
        => Resolve([new RootRequest(rootSlug, rootRange)], store);

    /// <summary>
    /// Resolves several roots together. Provisioning installs a whole plan's
    /// worth of Features at once, and resolving them one at a time would let two
    /// of them settle on incompatible versions of a shared dependency before
    /// anyone noticed (docs/store-provisioning.md).
    /// </summary>
    public ResolutionResult Resolve(IReadOnlyList<RootRequest> roots, StoreCompatibilityContext store)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(store);

        if (roots.Count == 0)
        {
            return ResolutionResult.Succeeded([]);
        }

        var constraints = new Dictionary<string, List<Constraint>>(StringComparer.Ordinal);
        var chosen = new Dictionary<string, RegistryVersion>(StringComparer.Ordinal);
        var rootSlugs = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<ResolutionFailure>();

        foreach (var root in roots)
        {
            var slug = FeatureSlug.Normalize(root.Slug);
            rootSlugs.Add(slug);
            AddConstraint(constraints, slug, new Constraint(root.Range, "the request"));
        }

        // The bound: each pass can only add constraints introduced by a newly
        // chosen version, and there are finitely many features to choose.
        var maxPasses = _features.Count + roots.Count + 1;

        for (var pass = 0; pass <= maxPasses; pass++)
        {
            var changed = false;

            foreach (var slug in constraints.Keys.ToList())
            {
                var selection = Select(slug, constraints[slug], store, failures);
                if (selection is null)
                {
                    continue;
                }

                if (chosen.TryGetValue(slug, out var current) && current.VersionId == selection.VersionId)
                {
                    continue;
                }

                chosen[slug] = selection;
                changed = true;

                foreach (var dependency in selection.Manifest.Dependencies.Features)
                {
                    AddConstraint(
                        constraints,
                        dependency.Slug,
                        new Constraint(dependency.Version, $"{slug} {selection.Version}"));
                }
            }

            if (!changed)
            {
                break;
            }

            if (pass == maxPasses)
            {
                failures.Add(new ResolutionFailure(
                    ResolutionFailureCode.ConflictingConstraints,
                    string.Join(", ", constraints.Keys.Order(StringComparer.Ordinal)),
                    "Dependency resolution did not settle. The declared ranges are mutually unsatisfiable."));
            }
        }

        if (failures.Count > 0)
        {
            return ResolutionResult.Failed(Deduplicate(failures));
        }

        var ordered = TopologicalOrder(chosen, out var cycle);
        if (cycle is not null)
        {
            return ResolutionResult.Failed(new ResolutionFailure(
                ResolutionFailureCode.DependencyCycle,
                string.Join(" -> ", cycle),
                $"These Features depend on each other in a cycle: {string.Join(" -> ", cycle)}."));
        }

        var steps = new List<PlanStep>(ordered.Count);

        foreach (var slug in ordered)
        {
            var version = chosen[slug];
            var feature = _features[slug];
            store.InstalledFeatures.TryGetValue(slug, out var installed);

            var action = installed switch
            {
                null => PlanAction.Install,
                _ when installed == version.Version => PlanAction.AlreadySatisfied,
                _ when installed < version.Version => PlanAction.Upgrade,
                _ => PlanAction.DowngradeRefused,
            };

            if (action is PlanAction.DowngradeRefused)
            {
                failures.Add(new ResolutionFailure(
                    ResolutionFailureCode.DowngradeRefused,
                    slug,
                    $"The store already runs {slug} {installed}, which is newer than the {version.Version} this plan resolves to. " +
                    "KNIGHT does not downgrade an installed Feature to satisfy a dependency."));
                continue;
            }

            // Compatibility is checked only for steps that would actually run.
            // A Feature already installed and satisfied is not re-litigated:
            // the store is demonstrably running it.
            if (action is not PlanAction.AlreadySatisfied)
            {
                CheckCompatibility(feature, version, store, failures);
            }

            steps.Add(new PlanStep(
                feature.FeatureId,
                version.VersionId,
                slug,
                feature.Name,
                version.Version,
                installed,
                action,
                rootSlugs.Contains(slug),
                DescribeRequirement(constraints[slug], rootSlugs.Contains(slug))));
        }

        return failures.Count > 0
            ? ResolutionResult.Failed(Deduplicate(failures))
            : ResolutionResult.Succeeded(steps);
    }

    /// <summary>
    /// Picks the version of one Feature that satisfies every constraint on it.
    ///
    /// The already-installed version wins ties: if what the store is running
    /// already satisfies everything asked of it, resolution keeps it rather than
    /// planning an upgrade nobody requested. An install of one Feature should not
    /// quietly bump three others.
    /// </summary>
    private RegistryVersion? Select(
        string slug,
        List<Constraint> constraints,
        StoreCompatibilityContext store,
        List<ResolutionFailure> failures)
    {
        if (!_features.TryGetValue(slug, out var feature))
        {
            failures.Add(new ResolutionFailure(
                ResolutionFailureCode.UnknownFeature,
                slug,
                $"'{slug}' is required by {DescribeRequirement(constraints, isRoot: false)} but is not registered."));
            return null;
        }

        if (feature.Status is FeatureStatus.Withdrawn)
        {
            failures.Add(new ResolutionFailure(
                ResolutionFailureCode.FeatureWithdrawn,
                slug,
                $"'{slug}' has been withdrawn and cannot be installed."));
            return null;
        }

        var candidates = feature.Versions.Where(version => version.IsInstallable).ToList();
        if (candidates.Count == 0)
        {
            failures.Add(new ResolutionFailure(
                ResolutionFailureCode.NoMatchingVersion,
                slug,
                $"'{slug}' has no published version to install."));
            return null;
        }

        var satisfying = candidates
            .Where(candidate => constraints.TrueForAll(constraint => constraint.Range.Includes(candidate.Version)))
            .ToList();

        if (satisfying.Count == 0)
        {
            var code = constraints.Count > 1
                ? ResolutionFailureCode.ConflictingConstraints
                : ResolutionFailureCode.NoMatchingVersion;

            var wanted = string.Join(
                " and ",
                constraints.Select(constraint => $"'{constraint.Range}' (required by {constraint.RequiredBy})"));

            var available = string.Join(", ", candidates.Select(candidate => candidate.Version.ToString()).Order(StringComparer.Ordinal));

            failures.Add(new ResolutionFailure(
                code,
                slug,
                $"No published version of '{slug}' satisfies {wanted}. Published versions: {available}."));
            return null;
        }

        if (store.InstalledFeatures.TryGetValue(slug, out var installed))
        {
            var keep = satisfying.FirstOrDefault(candidate => candidate.Version == installed);
            if (keep is not null)
            {
                return keep;
            }
        }

        return satisfying.MaxBy(candidate => candidate.Version, Comparer<SemanticVersion>.Default);
    }

    private static void CheckCompatibility(
        RegistryFeature feature,
        RegistryVersion version,
        StoreCompatibilityContext store,
        List<ResolutionFailure> failures)
    {
        if (feature.RequiresDedicatedInfrastructure && !store.HasDedicatedInfrastructure)
        {
            failures.Add(new ResolutionFailure(
                ResolutionFailureCode.DedicatedInfrastructureRequired,
                feature.Slug,
                $"'{feature.Slug}' requires dedicated infrastructure and this store runs on shared hosting."));
        }

        var compatibility = version.Manifest.Compatibility;

        CheckRange(feature.Slug, "store version", store.StoreVersion, compatibility.StoreVersion, version, failures);
        CheckRange(feature.Slug, "Python version", store.PythonVersion, compatibility.Python, version, failures);
        CheckRange(feature.Slug, "Django version", store.DjangoVersion, compatibility.Django, version, failures);
    }

    /// <summary>
    /// Checks one reported runtime fact against one manifest constraint.
    ///
    /// An unreported fact is not treated as a pass. A store that has never told
    /// KNIGHT its Django version is a store KNIGHT cannot certify a Feature
    /// against, and installing anyway on the grounds that nothing contradicted
    /// the constraint is exactly the optimism the compatibility check exists to
    /// remove. The exception is a constraint that admits everything: there is
    /// nothing to check, so nothing is required.
    /// </summary>
    private static void CheckRange(
        string slug,
        string what,
        string? reported,
        VersionRange range,
        RegistryVersion version,
        List<ResolutionFailure> failures)
    {
        if (range.IsUnbounded)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reported))
        {
            failures.Add(new ResolutionFailure(
                ResolutionFailureCode.IncompatibleStore,
                slug,
                $"{slug} {version.Version} requires a {what} of '{range}', and the store has not reported its {what}."));
            return;
        }

        if (!SemanticVersion.TryParse(PadForComparison(reported), out var parsed))
        {
            failures.Add(new ResolutionFailure(
                ResolutionFailureCode.IncompatibleStore,
                slug,
                $"The store reported a {what} of '{reported}', which cannot be compared against '{range}'."));
            return;
        }

        if (!range.Includes(parsed))
        {
            failures.Add(new ResolutionFailure(
                ResolutionFailureCode.IncompatibleStore,
                slug,
                $"{slug} {version.Version} requires a {what} of '{range}' and the store reports '{reported}'."));
        }
    }

    /// <summary>
    /// Pads a reported runtime version to three components. Stores report Python
    /// as "3.12" and Django as "5.1" because that is what those projects call
    /// themselves; refusing to compare them would make every manifest constraint
    /// on a runtime unusable.
    /// </summary>
    private static string PadForComparison(string reported)
    {
        var text = reported.Trim();
        var marker = text.IndexOfAny(['-', '+']);
        var core = marker >= 0 ? text[..marker] : text;
        var suffix = marker >= 0 ? text[marker..] : string.Empty;

        var components = core.Split('.').Length;
        return components >= 3
            ? text
            : core + string.Concat(Enumerable.Repeat(".0", 3 - components)) + suffix;
    }

    /// <summary>
    /// Orders the chosen versions so that every Feature comes after everything it
    /// depends on. Kahn's algorithm, and what is left over when it stalls is
    /// precisely the cycle — which is reported as a cycle rather than as an
    /// arbitrary order that would deadlock the installer instead.
    /// </summary>
    private static List<string> TopologicalOrder(
        IReadOnlyDictionary<string, RegistryVersion> chosen,
        out List<string>? cycle)
    {
        cycle = null;

        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var slug in chosen.Keys)
        {
            dependencies[slug] = [];
            dependents[slug] = [];
        }

        foreach (var (slug, version) in chosen)
        {
            foreach (var dependency in version.Manifest.Dependencies.Features)
            {
                if (!chosen.ContainsKey(dependency.Slug))
                {
                    continue;
                }

                if (dependencies[slug].Add(dependency.Slug))
                {
                    dependents[dependency.Slug].Add(slug);
                }
            }
        }

        // Ready nodes are drained in slug order so that two runs over the same
        // registry produce the same plan. A plan that reorders itself between
        // the preview and the job is a plan nobody can review.
        var ready = new PriorityQueue<string, string>();
        foreach (var (slug, edges) in dependencies)
        {
            if (edges.Count == 0)
            {
                ready.Enqueue(slug, slug);
            }
        }

        var ordered = new List<string>(chosen.Count);

        while (ready.TryDequeue(out var slug, out _))
        {
            ordered.Add(slug);

            foreach (var dependent in dependents[slug])
            {
                dependencies[dependent].Remove(slug);
                if (dependencies[dependent].Count == 0)
                {
                    ready.Enqueue(dependent, dependent);
                }
            }
        }

        if (ordered.Count != chosen.Count)
        {
            cycle = [.. chosen.Keys.Except(ordered, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            return [];
        }

        return ordered;
    }

    private static void AddConstraint(
        Dictionary<string, List<Constraint>> constraints,
        string slug,
        Constraint constraint)
    {
        if (!constraints.TryGetValue(slug, out var existing))
        {
            constraints[slug] = [constraint];
            return;
        }

        // The same range demanded by the same requirer twice is the diamond case
        // arriving on a second pass, not new information.
        if (!existing.Contains(constraint))
        {
            existing.Add(constraint);
        }
    }

    private static string DescribeRequirement(List<Constraint> constraints, bool isRoot) =>
        isRoot
            ? "the request"
            : string.Join(", ", constraints.Select(constraint => constraint.RequiredBy).Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Collapses repeats. The fixpoint can revisit a slug on several passes, and
    /// telling an operator the same thing three times makes the one new failure
    /// harder to find.
    /// </summary>
    private static List<ResolutionFailure> Deduplicate(List<ResolutionFailure> failures) =>
        [.. failures
            .GroupBy(failure => (failure.Code, failure.Slug, failure.Message))
            .Select(group => group.First())];

    private readonly record struct Constraint(VersionRange Range, string RequiredBy);
}

/// <summary>One Feature the caller asked for, with the versions it will accept.</summary>
public sealed record RootRequest(string Slug, VersionRange Range)
{
    public static RootRequest Latest(string slug) => new(slug, VersionRange.Any);
}
