# The .NET store agent

Everything an ASP.NET Core store needs to connect to KNIGHT and take delivery of
Features. **Written once, used by every .NET store** — it is a library, not a
per-project integration.

```csharp
builder.Services.AddKnightStoreAgent(builder.Configuration);
```

That line, a credential, and a restart. There is no per-store code to write.

## Which agent does a store need?

One per **stack**, not one per project. There are three reference agents in this
repository and a store uses whichever matches what it is built on:

| Store is built on | Agent | Where |
|---|---|---|
| Django | `knight_integration` | [`stores/reference-store`](../reference-store) |
| Node | `src/knight` | [`stores/node-reference-store`](../node-reference-store) |
| ASP.NET Core | `Knight.StoreAgent` | here |

Two ASP.NET Core stores share this one library. A third would too.

## What it does

The eight verbs KNIGHT's contract is written in, plus the seven more its
pipelines actually name — fifteen in all, because a store that knows fourteen
refuses the fifteenth and the job fails for a reason nobody can act on:

`preflight` · `fetch` · `verify` · `backup` · `install` · `create-extensions` ·
`migrate` · `configure` · `enable` · `disable` · `reload` · `healthcheck` ·
`restore-package` · `reverse-migrate` · `remove-package`

Plus the transport: handshake, heartbeat, claim, report each step as it
finishes, report the outcome. Outbound only — the store asks for work and KNIGHT
never connects inward, so a store can sit behind a firewall with no inbound port
and still receive Features.

## What it deliberately does not do

**It does not touch your database.** `migrate` records what state the Feature's
schema is in, under the namespace the manifest declares, and stops there. A
delivered assembly that opened the store's database and applied its own
migrations would be a Feature with more authority than the application hosting
it. A store that wants migrations run wires them to its own EF context against
that same namespace.

`create-extensions` refuses outright when a Feature declares one. Succeeding
without a database would tell KNIGHT the store is ready for a Feature that fails
the moment it runs.

**It does not restart you.** An assembly already loaded stays loaded, so `reload`
reports that the Feature is installed and served after a restart rather than
claiming to have reloaded. Saying otherwise would be a lie that surfaces as a 404
a merchant reports.

**It does not decide what is installed.** `FeatureRegistryAccessor` is read-only
from the store's side. What a store has is decided by delivery.

## Connecting a store

### 1. Reference the project

```xml
<ProjectReference Include="../../knight/stores/dotnet-store-agent/src/Knight.StoreAgent/Knight.StoreAgent.csproj" />
```

Or vendor the four source files; it has no dependencies beyond the framework.

### 2. Register it

```csharp
builder.Services.AddKnightStoreAgent(builder.Configuration);
```

### 3. Configure it

```json
{
  "Knight": {
    "BaseUrl": "https://knight.example.com",
    "Environment": "Production",
    "StoreVersion": "1.4.2",
    "FeatureRoot": "/var/lib/mystore/knight-features",
    "SigningKeys": { "dev": "<base64 SubjectPublicKeyInfo DER>" }
  }
}
```

`ClientId` and `ClientSecret` come from the environment, never the file:

```bash
Knight__ClientId=...
Knight__ClientSecret=...
```

**`FeatureRoot` must be a volume that survives a restart.** A container that
mounts nothing there loses every installed Feature on every deploy, and the
failure looks like KNIGHT forgetting rather than like a missing mount.

**`SigningKeys` is configuration and never anything a job carries.** A store that
took the key from the same message as the signature would have checked only that
the message agrees with itself. Startup fails when it is empty, because a store
that can verify nothing it downloads should not run.

### 4. Ask KNIGHT for the store's credential

The operator side, once per store:

```bash
curl -X POST "$KNIGHT/api/v1/stores/$STORE_ID/credentials" -H "Authorization: Bearer $TOKEN"
```

The secret is returned **exactly once, at issue**. It is not retrievable
afterwards; a lost one is rotated, not recovered.

### 5. Start the store

It handshakes, heartbeats `{"name": "dotnet", "dotnet": "..."}`, and begins
polling. The name is the part that matters: KNIGHT decides from it which
compatibility checks apply, and a store that omits it is refused rather than
assumed to be Django.

## Serving what was delivered

The registry says what this store has and whether it may serve it:

```csharp
app.MapGet("/reports", async (FeatureRegistryAccessor knight) =>
    await knight.IsServingAsync("storefront-reports")
        ? Results.Ok(/* ... */)
        : Results.NotFound());
```

`IsServingAsync` is installed **and** enabled. The two are separate facts and a
store enforces both — installed code still refuses to run without a valid
entitlement, which is what makes "the subscription ended" mean something on the
day it happens rather than whenever somebody next redeploys.

## Writing a Feature for a .NET store

The manifest declares the runtime and the three neutral names:

```yaml
runtime: dotnet

dotnet:
  namespace: knight_storefront_reports
  assembly: Knight.Feature.StorefrontReports
  mount:
    type: Knight.Feature.StorefrontReports.Endpoints
    prefix: reports/

compatibility:
  dotnet: ">=8.0"
```

A `namespace` is what the schema is recorded under, an `assembly` is what the
store loads, a `mount` is where it serves — the same three facts Django spells
`app_label`, `installed_app` and `urls.include`
([`adr/0032`](../../docs/adr/0032-a-feature-declares-its-runtime.md) §3).

## Testing

```bash
dotnet test stores/dotnet-store-agent
```

Nineteen tests, and the ones worth knowing about are the refusals: a digest that
does not match, a signature by an untrusted key, a job for another runtime, a
step this store does not know, and an artifact that tries to write outside the
directory it was given. A signed artifact is still not permission to escape its
directory.
