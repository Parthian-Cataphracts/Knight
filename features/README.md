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
| `knight-feature-promotions` | Advanced Promotions: buy X get Y, bundles and stacking rules. Plain coupons are the base store's ([`adr/0024`](../docs/adr/0024-base-store-versus-optional-feature.md)). |
| `knight-feature-reviews-ratings` | Product reviews, ratings, replies and moderation. Ships templates and a stylesheet, so it also exercises asset delivery. |
| `knight-feature-advanced-search` | Full-text search over an index the store pushes documents into. PostgreSQL only. |
| `knight-feature-customer-segmentation` | Segments computed from the analytics event stream. Depends on `analytics-core >=1.1.0`, and is the pair that exercises dependency resolution. |
| `knight-feature-loyalty-rewards` | Points in lots with their own expiry, spent oldest-first, and tiers. The first Feature with a running balance. |
| `knight-feature-gift-cards` | Gift cards and store credit. This one is money: the ledger is the truth and there is no balance column anywhere. |
| `knight-feature-marketing-automation` | Triggered email campaigns. Installs switched off, in recording mode, and needs a named secret to send for real. |
| `knight-feature-ai-reports` | Findings computed by arithmetic, narrated optionally and under a spend cap ([`adr/0030`](../docs/adr/0030-what-store-data-may-reach-a-model-provider.md)). |
| `tools/knight_package.py` | Build, sign, validate and publish. |

A directory's name is the Python distribution; the slug KNIGHT sells and
delivers under is the short one in its manifest — `advanced-promotions`, not
`knight-feature-promotions` ([`adr/0029`](../docs/adr/0029-one-slug-for-the-catalogue-and-the-package.md)).
What is sold, and what belongs in the base store instead, is
[`docs/feature-catalog.md`](../docs/feature-catalog.md).

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
