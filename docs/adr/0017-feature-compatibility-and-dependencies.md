# 0017 — Feature versioning, compatibility, and dependency resolution

- Status: **Accepted**
- Date: 2026-08-18
- Depends on: [`0014`](0014-features-as-deployable-packages.md)

## Context

Store applications and Features version independently:

```
Store: Cafe 1, version 4.2.0
  Analytics 1.3.0
  AI Reports 2.0.1   (requires Analytics Core >= 1.2.0)
```

KNIGHT must prevent installing a Feature version that a store cannot run, and
must install dependencies in the right order.

## Decision

**Semantic versioning** for both Features and store applications.
`FeatureVersion` is immutable once published; a mistake is corrected by
publishing a new version and **yanking** the old one (yanked = not installable
anew, existing installs untouched and flagged).

Each version declares in its manifest:

```
compatibility.storeVersion   semver range, e.g. ">=4.0.0,<6.0.0"
compatibility.python/django  runtime ranges
dependencies.features        other Features with semver ranges
dependencies.python          third-party packages with ranges
```

Resolution happens in KNIGHT **before any job is created**:

1. Resolve the feature dependency graph transitively; cycles are a publish-time
   error, not an install-time surprise.
2. Produce a topologically ordered install plan.
3. Check every node against the store's reported version and runtime.
4. Detect conflicts with already-installed versions. A conflict is a **hard
   failure with an explanation** — KNIGHT never force-upgrades an unrelated
   Feature to satisfy a new one.
5. Check infrastructure requirements (dedicated-only Features on shared
   hosting are refused).

If resolution fails, no job is created. The entitlement can still exist — the
customer paid — and installation stays `NotInstalled` with a recorded blocking
reason shown in the dashboard.

Uninstall order is the reverse of install order, and a Feature that another
installed Feature depends on cannot be uninstalled while that dependent
remains.

Store upgrades are checked in the other direction: raising a store's version
must not orphan installed Features, so KNIGHT reports which installed Features
would fall out of their compatibility range before the store upgrade proceeds.

## Consequences

**Positive** — incompatible installs are impossible rather than merely
discouraged; failures are explained in terms of constraints, not stack traces;
the plan is computed centrally where the full picture exists.

**Negative** — a real resolver must be written and tested (diamond
dependencies, ranges, yanked versions); manifests become a strict contract
whose mistakes block releases; stores must reliably report their version and
runtime, so a store that lies or fails to report cannot receive installs.
