# Phase 13 — how it was verified, and what verifying it found

Phase 13 put three real Features through the delivery engine, in increasing
order of difficulty, plus an upgrade and a rollback drill. The point was never
the Features themselves — it was to find out what the delivery path does wrong
when something real goes through it.

It found three things. One of them meant **no Feature's URLs had ever been
mounted on any store**, and no test could have caught it.

---

## 1. What was built

| Slug | What it tests that the previous one did not |
|---|---|
| `reviews-ratings` | Templates and static assets inside a delivered package; a Feature with its own page |
| `advanced-search` | A GIN index and a `tsvector` column; a store-side call site that has to keep an index fresh |
| `analytics-core` 1.1.0 | An **upgrade** of an installed Feature that another installed Feature depends on |
| `customer-segmentation` | A Feature whose data comes from another **Feature** rather than from the base store |

`customer-segmentation` declares `analytics-core >=1.1.0,<2.0.0`, and that lower
bound is real rather than decorative: 1.0.x has no per-subject aggregation at
all, so a segment cannot be computed without it. Installing segmentation on a
store running analytics 1.0.0 is therefore an upgrade of one Feature sequenced
before the install of another — which is exactly the resolver behaviour the
phase set out to exercise.

---

## 2. What verifying it found

### The delivery path never told a store how to load a package

Opening the reviews page in a browser returned 404. The Feature was installed,
its migrations had run, its health check passed, and its routes were not
mounted.

`ManifestReader` parses `django.app_label`, `django.installed_app` and
`django.urls.{include,prefix}` out of every manifest — and then dropped all
four. They were never persisted onto the delivery projection, never put in the
agent job payload, and never recorded by the store's `enable` step. The store
filled the gap by deriving the module name from the slug with its hyphens
swapped.

That guess was right only while the two happened to match, and
[`adr/0029`](adr/0029-one-slug-for-the-catalogue-and-the-package.md) shortened
every slug in phase 12. After that the delivery path would have registered
`reviews_ratings` — a module no store can import — for a package whose module is
`knight_feature_reviews_ratings`.

The URL half is older and worse. `analytics-reports` has declared a urlconf and
shipped a `views.py` since phase 3.5, and **its endpoint has never been
reachable on any store**. Publishing succeeded, the job ran, every step reported
success, and the feature served nothing.

Fixed end to end: `DeliverableVersion` carries the Django block out of the
signed manifest (the same argument the migration policy is read that way — the
manifest is what the author signed), the job payload has it, and `enable`
records it instead of guessing. `/analytics/summary/` answers now.

**Why no test caught it:** nothing tested the agent job payload at all. The unit
suites construct their own resolver fixtures, and the integration suites publish
against features they seed themselves and never claim a job. There are four new
integration tests on `GetForDeliveryAsync` and four store-side regression tests
on `enable`.

### The local install command read a spelling no manifest has ever used

`knight_install_local` read flat `url_include` and `url_prefix` keys off the
`django` block. The manifest nests them under `django.urls`, and KNIGHT's own
reader parses them that way.

It went unnoticed because the store's hand-rolled YAML fallback **flattened the
nesting** — `django.urls.include` arrived as `django.include` — while its own
docstring claimed one level of nesting "is all the schema allows". Two wrong
spellings that never met.

The parser is indentation-aware to any depth now and explicit about what it
skips (sequences and block scalars, neither of which this command needs).
PyYAML is a development dependency, so the real parser is what runs in
development and in CI; the fallback is a genuine last resort rather than the
normal path.

### A stale `build/` directory resurrects deleted files into an installed package

Renaming a generated migration and reinstalling the package shipped **both** the
old file and the new one, and Django refused to migrate: two leaf nodes in the
graph. setuptools had kept the deleted file in `build/lib` and packaged it from
there.

`build/` was untracked and gitignored in phase 12, so a fresh checkout — which
is what CI installs from — cannot hit this. On a developer's machine it can, and
the failure names neither the cause nor the directory.

---

## 3. The upgrade drill

A store on `analytics-core` 1.0.0, with `analytics-reports` 1.0.0 installed
against it and 15 events recorded, upgraded in place to 1.1.0:

| Checked | Result |
|---|---|
| Events survive the upgrade | 15 before, 15 after |
| Historical events get no invented subject | all 15 have an empty subject |
| New per-subject aggregation works | three subjects, correct counts and totals |
| The dependent keeps working | `/analytics/summary/` answers unchanged |
| The migration reverses | `subject` column dropped, all events still present |
| And re-applies | clean |

The reverse is worth being precise about, and the manifest now says so: it puts
the *schema* back, and every subject written while 1.1.0 was installed goes with
the column. That is acceptable for an optional dimension on a log and would not
be for anything a customer typed — the distinction
[`adr/0016`](adr/0016-feature-migration-and-removal-policy.md) draws between
reversing a migration and reversing a business fact. It was demonstrated
accidentally: rolling back mid-drill emptied every segment, because the data
segmentation groups on had gone.

---

## 4. The rollback drill

Each Feature's migrations reversed to zero and re-applied cleanly —
`reviews-ratings`, `advanced-search`, `customer-segmentation`. That is the check
[`feature-authoring.md`](feature-authoring.md) demands of anything declaring
`migrations.reversible: true`, and all three declare it.

Then a deliberately broken migration: a valid `AddField` followed by an
`ALTER TABLE` that cannot work.

```
Applying knight_reviews.0002_deliberately_broken...
django.db.utils.ProgrammingError: column "drill_marker" of relation
"knight_review" already exists
```

Afterwards:

| Checked | Result |
|---|---|
| Column count | 15 before, 15 after |
| The half-applied `drill_marker` column | absent |
| Migration state | recorded unapplied |
| Health check | passes |
| The Feature | still serving |

Nothing was left half-migrated. **This property depends on two things and is
not free:** PostgreSQL's transactional DDL, and the migration being `atomic`. A
migration that sets `atomic = False` — which anything doing a long backfill
eventually wants to — has no such guarantee, and the store would be left in
exactly the half-state this drill did not produce.

---

## 5. Repeating it

Database up, from the repository root:

```bash
docker compose -f infrastructure/docker/docker-compose.yml up -d
```

In `stores/reference-store`, install and register the Features the way a store
gets them, then migrate:

```bash
python -m pip install ../../features/knight-feature-reviews-ratings ../../features/knight-feature-advanced-search ../../features/knight-feature-analytics-core ../../features/knight-feature-customer-segmentation
```

```bash
python manage.py knight_install_local ../../features/knight-feature-reviews-ratings ../../features/knight-feature-advanced-search ../../features/knight-feature-analytics-core ../../features/knight-feature-customer-segmentation
```

```bash
python manage.py migrate
```

Index the catalogue, then start the store:

```bash
python manage.py knight_reindex_search --rebuild
```

```bash
python manage.py runserver 8010
```

Open `http://localhost:8010/reviews/product/<id>/` — it should show an average,
a star distribution, a verified-purchase badge and a merchant reply, and should
say "No reviews yet" for a product with none. The other three answer as JSON:

```bash
curl "http://localhost:8010/search/?q=bergamot"
```

```bash
curl "http://localhost:8010/segments/"
```

```bash
curl "http://localhost:8010/analytics/summary/?days=2"
```

Then the suites:

```bash
REQUIRE_FEATURE_TESTS=1 python manage.py test
```

**Use Python 3.12.** Django 5.1 does not support 3.14 — its test client cannot
copy a template context there, so every test that renders a template errors with
`'super' object has no attribute 'dicts'`. CI runs 3.12; this repository now
carries a `.venv312` beside the 3.14 one for that reason.

---

## 6. Test results

| Suite | Result |
|---|---|
| Store, all Features installed, `REQUIRE_FEATURE_TESTS=1` | **274 passed**, 0 skipped (213 before) |
| Store, no Features installed at all | **274 passed**, 97 skipped — the optionality claim, checked |
| Backend unit | **590 passed** (584 before; 6 new resolver tests on the real graph) |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | **150 passed** (146 before; 4 new on the job payload) |

The 61 new store tests cover the three Features as *installed* — routes mounted
under the prefix their manifests declare, templates found inside their own
packages, the stylesheet served — plus hostile search queries, the segmentation
dependency boundary, and the runtime-wiring regression.

---

## 7. What is deliberately not covered

**No fuzzy matching in `advanced-search` 1.0.** Real typo tolerance wants
`pg_trgm`, and `CREATE EXTENSION` is a cluster-level change a rollback cannot
safely undo — another Feature may have started using the extension in the
meantime. It is not Class A, and this phase is Class A only. A prefix pass
covers the half-typed word, which is the case a search box actually shows.

**`advanced-search` requires PostgreSQL and the manifest cannot say so.**
`ManifestReader` reads `storeVersion`, `python` and `django` and ignores
anything else, so a `database:` key would look like a guarantee and be nothing.
The health check catches it at install instead, which is late but honest.
Making it enforceable is a schema change and is recorded in
[`risks.md`](risks.md).

**The uninstall guard is still untested.** `FeatureDeliveryService` refuses to
uninstall a Feature something else depends on, and nothing exercises that path;
the resolver half it rests on is covered, the refusal itself is not. It needs a
service-level test with a seeded installation, and it is carried into phase 14.

**None of this is reachable from the dashboard.** These are store-side
capabilities, and KNIGHT is not a store's business backend — but it means
moderation, reindexing and recomputation are shell commands today.
