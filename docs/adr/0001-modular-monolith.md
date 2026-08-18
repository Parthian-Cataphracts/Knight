# 0001. Modular Monolith Architecture

## Status

Accepted

## Context

The platform must support several independent business capabilities (identity,
tenancy, feature management, and later catalog, ordering, delivery,
reservations, and more) across a growing number of tenants. Microservices
would front-load operational complexity — service discovery, distributed
transactions, network-boundary latency — before there is any proven need to
scale or deploy those capabilities independently.

## Decision

Build a single deployable ASP.NET Core API composed of independent modules
(`modules/Identity`, `modules/Tenancy`, `modules/FeatureManagement`, and
future business modules), each with its own domain model and persistence
contracts, and enforce module boundaries via project references and
architecture tests rather than network boundaries.

## Consequences

- Faster iteration and simpler local development/deployment for the current
  stage of the product.
- Module boundaries are enforced at compile time and by
  `Knight.ArchitectureTests`, keeping the codebase ready for extraction.
- A module can be extracted into its own service later without a rewrite,
  because it already owns its domain model and does not reach into another
  module's internals.
- All modules currently share one PostgreSQL database and one deployment
  unit; scaling or deploying a single module independently is not possible
  without further work.
