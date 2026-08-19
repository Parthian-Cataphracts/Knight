# Features

Deployable KNIGHT Features. Each directory is a Python package that is built,
signed and published once, and then delivered by KNIGHT into every store whose
customer is entitled to it.

A Feature is **not** a boolean flag
([`adr/0014`](../docs/adr/0014-features-as-deployable-packages.md)).

## What is here

| | |
|---|---|
| `knight-feature-analytics-core` | Records events and rolls them into daily counters. Owns the event table. |
| `knight-feature-analytics-reports` | A reporting surface over that stream. Depends on the core, and exists to exercise dependency resolution against a real package. |
| `tools/knight_package.py` | Build, sign, validate and publish. |

## Getting started

Read [`docs/feature-authoring.md`](../docs/feature-authoring.md). To see the whole
pipeline work locally:

```bash
# A development signing pair. The public half goes into KNIGHT's
# FeatureArtifacts:Keys and into each store's KNIGHT_SIGNING_KEYS.
python features/tools/knight_package.py keygen

python features/tools/knight_package.py build features/knight-feature-analytics-core

KNIGHT_TOKEN=... KNIGHT_ARTIFACT_ROOT=./artifacts \
  python features/tools/knight_package.py publish features/knight-feature-analytics-core
```

Built artifacts land in `dist/` and the local package store is `artifacts/`. Both
are ignored by git: delivered code is built and signed, never committed.
