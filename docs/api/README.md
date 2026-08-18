# API Documentation

OpenAPI documents are generated at runtime (`/openapi/v1.json` in
development) and browsable via Scalar at `/scalar` when running the API in
the Development environment.

## Authentication

```
POST /api/platform/auth/login
POST /api/platform/auth/refresh
POST /api/platform/auth/logout
POST /api/platform/auth/logout-all
GET  /api/platform/auth/me
POST /api/platform/auth/change-password

POST /api/tenant/auth/login
POST /api/tenant/auth/refresh
POST /api/tenant/auth/logout
POST /api/tenant/auth/logout-all
GET  /api/tenant/auth/me
POST /api/tenant/auth/change-password
```

`login`/`refresh` are anonymous; the rest require the matching
`PlatformAdminOnly`/`TenantUserOnly` policy. See
`docs/architecture/authorization.md` for the full authentication/session
model (tokens, refresh rotation, cookies, lockout).

## Platform tenant management

```
POST   /api/platform/tenants
GET    /api/platform/tenants
GET    /api/platform/tenants/{id}
PUT    /api/platform/tenants/{id}
POST   /api/platform/tenants/{id}/activate
POST   /api/platform/tenants/{id}/suspend
POST   /api/platform/tenants/{id}/archive
POST   /api/platform/tenants/{id}/domains
DELETE /api/platform/tenants/{id}/domains/{domainId}
POST   /api/platform/tenants/{id}/domains/{domainId}/primary
POST   /api/platform/tenants/{id}/features/{featureKey}/enable
POST   /api/platform/tenants/{id}/features/{featureKey}/disable
```

All require `PlatformAdminOnly`.

## Tenant runtime

```
GET /api/tenant/me
```

Requires `TenantUserOnly` and a resolved tenant context.
`GET /api/tenant/auth/me` also returns the caller's current effective
permission keys.

## Tenant staff and role self-administration

```
GET    /api/tenant/staff                        tenant.users.view
GET    /api/tenant/staff/{id}                    tenant.users.view
POST   /api/tenant/staff                         tenant.users.create
POST   /api/tenant/staff/{id}/enable              tenant.users.enable
POST   /api/tenant/staff/{id}/disable             tenant.users.disable
POST   /api/tenant/staff/{id}/unlock              tenant.users.update
PUT    /api/tenant/staff/{id}/roles               tenant.users.roles.assign
POST   /api/tenant/staff/{id}/sessions/revoke      tenant.users.sessions.revoke

GET    /api/tenant/roles                          tenant.roles.view
GET    /api/tenant/roles/{id}                      tenant.roles.view
POST   /api/tenant/roles                          tenant.roles.create
PUT    /api/tenant/roles/{id}                      tenant.roles.update
PUT    /api/tenant/roles/{id}/permissions          tenant.roles.permissions.assign
DELETE /api/tenant/roles/{id}                      tenant.roles.delete

GET    /api/tenant/permissions                    tenant.roles.view
```

All require `TenantUserOnly` **and** the listed permission
(`.RequirePermission(...)`). Always scoped to the caller's own resolved
tenant — never accept a tenant selector from the request. Role/permission
grants are subject to the privilege-delegation rule — see
`docs/architecture/authorization.md`.

## Platform tenant staff and role management

```
GET    /api/platform/tenants/{tenantId}/staff
POST   /api/platform/tenants/{tenantId}/staff
POST   /api/platform/tenants/{tenantId}/staff/{userId}/enable
POST   /api/platform/tenants/{tenantId}/staff/{userId}/disable
PUT    /api/platform/tenants/{tenantId}/staff/{userId}/roles
POST   /api/platform/tenants/{tenantId}/staff/{userId}/sessions/revoke

GET    /api/platform/tenants/{tenantId}/roles
POST   /api/platform/tenants/{tenantId}/roles
PUT    /api/platform/tenants/{tenantId}/roles/{roleId}
PUT    /api/platform/tenants/{tenantId}/roles/{roleId}/permissions
DELETE /api/platform/tenants/{tenantId}/roles/{roleId}
```

All require `PlatformAdminOnly`. The target tenant comes from the route —
Platform context intentionally manages tenants explicitly. These call the
same application services as the Tenant self-administration routes above,
with the privilege-delegation check bypassed for the authenticated
PlatformAdmin principal.

## Tenant catalog administration

```
GET    /api/tenant/catalog/categories                        catalog.categories.view
POST   /api/tenant/catalog/categories                        catalog.categories.create
GET    /api/tenant/catalog/categories/{id}                    catalog.categories.view
PUT    /api/tenant/catalog/categories/{id}                    catalog.categories.update
PUT    /api/tenant/catalog/categories/{id}/visibility          catalog.categories.update
DELETE /api/tenant/catalog/categories/{id}                    catalog.categories.delete

GET    /api/tenant/catalog/products                          catalog.products.view
POST   /api/tenant/catalog/products                          catalog.products.create
GET    /api/tenant/catalog/products/{id}                      catalog.products.view
PUT    /api/tenant/catalog/products/{id}                      catalog.products.update
PUT    /api/tenant/catalog/products/{id}/category              catalog.products.update
POST   /api/tenant/catalog/products/{id}/activate              catalog.products.update
PUT    /api/tenant/catalog/products/{id}/visibility            catalog.products.update
PUT    /api/tenant/catalog/products/{id}/availability          catalog.availability.manage
DELETE /api/tenant/catalog/products/{id}                      catalog.products.delete
GET    /api/tenant/catalog/products/{productId}/modifier-groups catalog.modifiers.manage
PUT    /api/tenant/catalog/products/{productId}/modifier-groups catalog.modifiers.manage

GET    /api/tenant/catalog/products/{productId}/variants               catalog.products.view
POST   /api/tenant/catalog/products/{productId}/variants               catalog.variants.manage
GET    /api/tenant/catalog/products/{productId}/variants/{variantId}    catalog.products.view
PUT    /api/tenant/catalog/products/{productId}/variants/{variantId}    catalog.variants.manage
POST   /api/tenant/catalog/products/{productId}/variants/{variantId}/default      catalog.variants.manage
PUT    /api/tenant/catalog/products/{productId}/variants/{variantId}/availability  catalog.availability.manage
DELETE /api/tenant/catalog/products/{productId}/variants/{variantId}    catalog.variants.manage

GET    /api/tenant/catalog/modifier-groups                             catalog.modifiers.manage
POST   /api/tenant/catalog/modifier-groups                             catalog.modifiers.manage
GET    /api/tenant/catalog/modifier-groups/{id}                         catalog.modifiers.manage
PUT    /api/tenant/catalog/modifier-groups/{id}                         catalog.modifiers.manage
DELETE /api/tenant/catalog/modifier-groups/{id}                         catalog.modifiers.manage
GET    /api/tenant/catalog/modifier-groups/{groupId}/modifiers          catalog.modifiers.manage
POST   /api/tenant/catalog/modifier-groups/{groupId}/modifiers          catalog.modifiers.manage
GET    /api/tenant/catalog/modifier-groups/{groupId}/modifiers/{modifierId}  catalog.modifiers.manage
PUT    /api/tenant/catalog/modifier-groups/{groupId}/modifiers/{modifierId}  catalog.modifiers.manage
PUT    /api/tenant/catalog/modifier-groups/{groupId}/modifiers/{modifierId}/availability  catalog.modifiers.manage

GET    /api/tenant/catalog/products/{productId}/media                   catalog.media.manage
POST   /api/tenant/catalog/products/{productId}/media                   catalog.media.manage
POST   /api/tenant/catalog/products/{productId}/media/{mediaId}/primary  catalog.media.manage
DELETE /api/tenant/catalog/products/{productId}/media/{mediaId}          catalog.media.manage
```

All require `TenantUserOnly`, the listed permission, **and** the tenant's
`catalog` feature — the feature is checked explicitly as the first step of every
handler and denies with 403 independently of the permission. Always scoped to
the caller's own resolved tenant. `DELETE` on a product archives it (200 with
the archived state) and on a variant deactivates it (204); `DELETE` on a
category or a modifier group is a physical delete that returns 409 while
anything still references it. See `docs/architecture/catalog.md`.

## Platform tenant catalog management

```
GET    /api/platform/tenants/{tenantId}/catalog/categories
POST   /api/platform/tenants/{tenantId}/catalog/categories
GET    /api/platform/tenants/{tenantId}/catalog/categories/{categoryId}
PUT    /api/platform/tenants/{tenantId}/catalog/categories/{categoryId}
PUT    /api/platform/tenants/{tenantId}/catalog/categories/{categoryId}/visibility
DELETE /api/platform/tenants/{tenantId}/catalog/categories/{categoryId}

GET    /api/platform/tenants/{tenantId}/catalog/products
POST   /api/platform/tenants/{tenantId}/catalog/products
GET    /api/platform/tenants/{tenantId}/catalog/products/{productId}
PUT    /api/platform/tenants/{tenantId}/catalog/products/{productId}
PUT    /api/platform/tenants/{tenantId}/catalog/products/{productId}/category
POST   /api/platform/tenants/{tenantId}/catalog/products/{productId}/activate
PUT    /api/platform/tenants/{tenantId}/catalog/products/{productId}/visibility
PUT    /api/platform/tenants/{tenantId}/catalog/products/{productId}/availability
DELETE /api/platform/tenants/{tenantId}/catalog/products/{productId}
GET    /api/platform/tenants/{tenantId}/catalog/products/{productId}/modifier-groups
PUT    /api/platform/tenants/{tenantId}/catalog/products/{productId}/modifier-groups

GET    /api/platform/tenants/{tenantId}/catalog/products/{productId}/variants
POST   /api/platform/tenants/{tenantId}/catalog/products/{productId}/variants
GET    /api/platform/tenants/{tenantId}/catalog/products/{productId}/variants/{variantId}
PUT    /api/platform/tenants/{tenantId}/catalog/products/{productId}/variants/{variantId}
POST   /api/platform/tenants/{tenantId}/catalog/products/{productId}/variants/{variantId}/default
PUT    /api/platform/tenants/{tenantId}/catalog/products/{productId}/variants/{variantId}/availability
DELETE /api/platform/tenants/{tenantId}/catalog/products/{productId}/variants/{variantId}

GET    /api/platform/tenants/{tenantId}/catalog/modifier-groups
POST   /api/platform/tenants/{tenantId}/catalog/modifier-groups
GET    /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}
PUT    /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}
DELETE /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}
GET    /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}/modifiers
POST   /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}/modifiers
GET    /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}/modifiers/{modifierId}
PUT    /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}/modifiers/{modifierId}
PUT    /api/platform/tenants/{tenantId}/catalog/modifier-groups/{groupId}/modifiers/{modifierId}/availability

GET    /api/platform/tenants/{tenantId}/catalog/products/{productId}/media
POST   /api/platform/tenants/{tenantId}/catalog/products/{productId}/media
POST   /api/platform/tenants/{tenantId}/catalog/products/{productId}/media/{mediaId}/primary
DELETE /api/platform/tenants/{tenantId}/catalog/products/{productId}/media/{mediaId}
```

All require `PlatformAdminOnly` and no `catalog.*` permission — the policy is
the authority. The target tenant comes from the route, and that tenant's
`catalog` feature is still enforced, so a platform admin cannot administer a
capability the tenant does not have. These call the same application services as
the tenant self-administration routes above.

## Public catalog

```
GET /api/catalog/categories            anonymous, feature-gated
GET /api/catalog/categories/{slug}     anonymous, feature-gated
GET /api/catalog/products              anonymous, feature-gated
GET /api/catalog/products/{slug}       anonymous, feature-gated
```

No authorization policy applies: the tenant is resolved from the request host
before authorization runs, and a host with no resolvable tenant fails closed.
`/products` supports `page`, `pageSize`, `categoryId` and `search`;
`/categories` supports `page` and `pageSize`. Responses use dedicated public
shapes carrying no lifecycle, audit or internal fields, and only visible,
non-draft, non-archived entries are ever returned — see
`docs/architecture/catalog.md`.

This directory is reserved for further hand-written API guides once more
business endpoints exist.
