# 0031 — Database extensions are declared, not migrated

- Status: **Accepted**
- Date: 2026-08-26
- Amends: [`0016`](0016-feature-migration-and-removal-policy.md)

## Context

Two Features want a PostgreSQL extension.

`advanced-search` has wanted `pg_trgm` since phase 13. Real typo tolerance is
trigram similarity, and without it the Feature can offer a prefix pass and
nothing more — a shopper who types `expresso` finds nothing. Phase 16's
`advanced-inventory` wants the same extension for supplier and SKU lookup, and
`restaurant-operations` wants `btree_gist`, without which "two seatings may not
overlap on one table" is application code hoping it holds rather than a
constraint that cannot be violated.

Each time the question came up it was deferred with the same sentence: a
`CREATE EXTENSION` is not the kind of migration that phase's rules allowed. That
sentence was correct and it was never finished. It has to be finished now,
because it is about to have two callers and a rule invented twice is two rules.

The difficulty is specific. `CREATE EXTENSION` is not a change to a Feature's
own tables. It is a change to the **database**, shared by the store and by every
other Feature installed in it. Django's own `CreateExtension` operation reverses
to `DROP EXTENSION`, and that reverse is the problem: by the time
`advanced-search` rolls back, `advanced-inventory` may have been installed for a
month and be indexing supplier names with the same extension. A rollback that
reverses its own migration honestly would break a Feature it has never heard of.

Nothing in [`0016`](0016-feature-migration-and-removal-policy.md) covers this.
Its rules are about a Feature's own app label, where "reversible" means "the
reverse restores what was there". An extension has no such reverse: putting it
back is easy, and knowing whether it should come out at all is impossible from
inside one Feature.

## Decision

### 1. Three classes, written down once

The manifests have been carrying the words `Class A` and `Class C` in comments
since phase 13 without either being defined anywhere. They are defined here, and
the middle one is new.

| Class | What it is | Reverse | Where it may appear |
|---|---|---|---|
| **A** | Additive changes to the Feature's own tables: `CreateModel`, `AddField` with a default, `AddIndex`, `AddConstraint` | Genuinely restores the previous schema | Any migration. `reversible: true` |
| **B** | Additive changes to **shared** database state the Feature does not own — today, exactly `CREATE EXTENSION` | Deliberately does nothing | Declared in `migrations.extensions`. Does not affect `reversible` |
| **C** | Anything lossy or transforming: `RemoveField`, `AlterField` that narrows, a data migration, anything touching money, points or consent | No honest reverse exists | `reversible: false`, and KNIGHT stops rather than guessing |

Class B is the whole of this decision. A and C were already the practice; the
gap was that everything which was not A had to be treated as C, which is why
`pg_trgm` waited three phases behind a rule meant for dropped columns.

### 2. An extension is declared in the manifest, not written in a migration

```yaml
migrations:
  required: true
  reversible: true
  extensions:
    - pg_trgm
```

Declared rather than written, for four reasons, and the first is the one that
matters:

- **KNIGHT can refuse it, and so can the store.** The list is closed
  (`ManifestReader.SupportedExtensions`) and checked at publish, then checked
  again by the store's own step against its own copy of the list. Some PostgreSQL
  extensions — `plpython3u`, `plperlu`, `file_fdw`, `dblink` — amount to
  arbitrary code execution against the database owner, and none of them is a
  thing a Feature has any business asking for. Be precise about what this
  buys: a package is arbitrary Python and its migrations could contain anything,
  so the publish-time list is a check on carelessness rather than a sandbox —
  what protects a store from a hostile *package* is the signature. What the
  store's own list protects against is different and real: **the job body is not
  signed**, so a control plane that has been compromised or has simply moved on
  cannot talk a store into creating something outside its list.
- **Declaring one implies the engine.** A manifest with `extensions` and without
  `compatibility.database: postgresql` is refused. The Feature that forgets is
  the Feature that installs onto SQLite and fails halfway through a migration,
  which is exactly the failure `compatibility.database` was added in phase 14 to
  prevent.
- **It happens before anything has changed.** The `create-extensions` step runs
  after `install` and before `migrate`. A database user without the privilege —
  which is most managed PostgreSQL — fails there, with a message naming the
  extension and the statement an administrator must run, rather than eight
  operations into a migration.
- **The operator sees it before confirming.** It is part of the migration policy
  KNIGHT surfaces, alongside the estimated duration and the maintenance-window
  flag.

### 3. It is created idempotently and never dropped

`CREATE EXTENSION IF NOT EXISTS`, on install, on upgrade, on every retry.

**No rollback, uninstall or purge ever drops an extension.** Not the store's
`create-extensions` step, which has no entry in the rollback table; not a
Feature's own migration, which is why the extension is not written there. If a
store ends up with `pg_trgm` present and nothing using it, the cost is a
catalogue entry and some kilobytes. If a rollback drops one another Feature is
using, the cost is that Feature's queries failing until somebody works out why.
Those are not comparable, and the asymmetry decides it.

Removing an unused extension is an operator's decision, taken with knowledge of
the whole database. It is a line in the runbook, not a step in a job.

### 4. A migration may ensure an extension, and may never drop one

The migration that needs the extension opens with the same idempotent statement
the install step runs:

```python
migrations.RunSQL(
    sql='CREATE EXTENSION IF NOT EXISTS "pg_trgm"',
    reverse_sql=migrations.RunSQL.noop,   # never dropped: adr/0031
),
```

Twice is not redundancy, because the two callers are different. The step is the
**delivery** path: it runs before anything has changed, from a list the store
holds, and it turns a missing privilege into a message naming the statement to
run. The migration is the **everything else** path: `manage.py migrate` on a
developer's checkout, and — the case that decides it — Django's test database,
which is created and migrated by the test runner with no installer anywhere near
it. A Feature whose extension existed only in the manifest would have a suite
that passes on the machine where somebody once created it by hand.

What is forbidden is the reverse. `django.contrib.postgres.operations.CreateExtension`
reverses to `DROP EXTENSION` and is therefore not usable here; `RunSQL` with an
explicit `noop` reverse says the same thing and says why.

The manifest declaration is not optional because the migration exists. The
declaration is the contract — it is what KNIGHT validates, what the operator
sees, and what the store acts on — and a migration creating an extension the
manifest does not declare is a Feature lying about what it needs.

### 5. Reversibility is unaffected

A Feature whose only non-Class-A operation is a declared extension still declares
`reversible: true`, and it is telling the truth. Reversing runs every operation
backwards; the Class B one is a no-op by construction, and what is left restores
the schema exactly.

This is the practical payoff. `advanced-search` 1.1.0 adds a trigram index and
stays reversible, three phases after `pg_trgm` was first deferred for being
something a rollback could not undo.

## Consequences

**Positive** — `pg_trgm` and `btree_gist` become usable, by two Features
independently, without either one's rollback endangering the other. The
dangerous class of extension is refused at publish rather than reviewed by
hoping somebody reads the migration. The privilege failure that managed
PostgreSQL actually produces happens before the database has been touched and
says what to do about it.

**Negative** — extensions accumulate: a store that installed and removed a
Feature keeps the extension forever unless an operator removes it. KNIGHT's
allow-list is a release-blocking bottleneck for a Feature author who needs an
extension that is not on it, which is intended and will be irritating. And the
list is enforced twice — at publish and again in the store's own step — which is
duplication on purpose: the store does not trust the job body, for the same
reason it verifies a signature it already asked for.

**Not decided here** — extensions on engines other than PostgreSQL. `mysql` and
`sqlite` are valid values of `compatibility.database` and neither has anything
resembling this. A Feature declaring extensions must declare PostgreSQL, and if
that ever changes it is a new decision rather than a widened field.
