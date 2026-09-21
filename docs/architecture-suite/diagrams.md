# KNIGHT — Diagrams (generated summary)

> Generated from the codebase. Mermaid renders on GitHub and most Markdown viewers.
> Simplified on purpose — the real schema has more columns; see
> `Knight.Infrastructure/ControlPlane/ControlPlaneDbContext.cs` for the full set.

## 1. ERD — the core control-plane entities

What to look at: a **Customer** owns **Stores** and holds a **Subscription** to a
**Plan**; the Plan bundles **Features**; each Feature has versioned packages that
get **installed** onto a Store; buying queues a **ProvisioningJob**.

```mermaid
erDiagram
  CUSTOMER ||--o{ STORE : owns
  CUSTOMER ||--o| SUBSCRIPTION : holds
  CUSTOMER ||--o{ FEATURE_ENTITLEMENT : granted
  PLAN ||--o{ SUBSCRIPTION : sold_as
  PLAN ||--o{ PLAN_FEATURE : bundles
  FEATURE ||--o{ PLAN_FEATURE : in
  FEATURE ||--o{ FEATURE_VERSION : versioned
  FEATURE ||--o{ FEATURE_ENTITLEMENT : entitles
  STORE ||--o{ FEATURE_INSTALLATION : has
  FEATURE ||--o{ FEATURE_INSTALLATION : of
  STORE ||--o{ PROVISIONING_JOB : provisioned_by
  STORE ||--o{ STORE_CREDENTIAL : authenticates
  SERVER ||--o{ STORE : hosts

  CUSTOMER { uuid Id PK
             string Email
             string Status }
  STORE { uuid Id PK
          uuid CustomerId FK
          uuid ServerId FK
          string PrimaryDomain
          string Status }
  PLAN { uuid Id PK
         string Key
         numeric BasePriceAmount
         string Currency }
  SUBSCRIPTION { uuid Id PK
                 uuid CustomerId FK
                 uuid PlanId FK
                 string Status }
  FEATURE { uuid Id PK
            string Slug
            bool IsOptional }
  FEATURE_VERSION { uuid Id PK
                    uuid FeatureId FK
                    string Version }
  FEATURE_INSTALLATION { uuid Id PK
                         uuid StoreId FK
                         uuid FeatureId FK
                         string State }
  PROVISIONING_JOB { uuid Id PK
                     uuid StoreId FK
                     string State }
```

## 2. Sequence — self-service happy path (register → running store)

What to look at: payment is what drives provisioning; no operator step in the
middle. The store's base Features come from its image; only optional Features are
delivered.

```mermaid
sequenceDiagram
  actor Merchant
  participant Portal as React Portal
  participant API as KNIGHT API
  participant Pay as Payment (simulated)
  participant Prov as Provisioning worker
  participant Store as New store

  Merchant->>Portal: register + verify email
  Merchant->>Portal: choose plan, pay
  Portal->>API: POST /billing/checkout
  API-->>Portal: checkoutUrl
  Merchant->>Pay: complete payment
  Pay->>API: webhook payment_succeeded
  API->>API: activate subscription, grant entitlements
  API->>Prov: queue ProvisioningJob
  Prov->>Store: server, agent, credential, domain, base image
  Prov->>Store: install entitled optional Features
  Prov-->>API: job Succeeded → store Active
  Merchant->>Portal: sees store «آماده»
```

## 3. Component — the shape of the system

What to look at: one modular-monolith API in the middle; independent stores and
feature-services around it; a shared PostgreSQL for the control plane.

```mermaid
flowchart TD
  Dash[React dashboard + portal] -->|/api/v1| API
  subgraph API[KNIGHT API - modular monolith]
    Onb[Onboarding] --> Sub[Subscriptions]
    Sub --> Ent[Entitlements]
    Ent --> Prov[Provisioning]
    Prov --> Del[FeatureDelivery]
    Reg[FeatureRegistry] --> Del
    Obs[Observability/Ingestion]
    Bill[PlatformBilling]
  end
  API --> DB[(PostgreSQL control schema)]
  Prov -->|bootstraps| Store[(Independent store apps)]
  Del -->|signed proxy| Svc[external_service Features - FastAPI]
  Store -->|health, logs, events| Obs
```
