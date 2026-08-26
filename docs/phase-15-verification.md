# Phase 15 — how it was verified, and what verifying it found

Phase 15 gave KNIGHT the ability to act on a store's behalf without anybody
asking, and then built the two Features that need it. Its exit criterion was
narrow and worth repeating: **KNIGHT can act on a schedule on a store's behalf,
with per-customer cost bounded and auditable.**

The order mattered. Phase 14 wanted a scheduled job for loyalty expiry, found
that the manifest schema had no concept of one, and refused to declare a
`workers:` block that would have looked like a guarantee and scheduled nothing.
This phase built it first, and everything after it uses it.

---

## 1. What was built

| | |
|---|---|
| **Manifest-declared workers** | A Feature declares a scheduled job; KNIGHT delivers the declaration with the install; the store runs it. Installing a Feature installs its schedule |
| **`marketing-automation`** | Welcome, post-purchase, abandoned-cart and win-back campaigns. The first Feature needing a third-party credential |
| **`ai-reports`** | Automated interpretation of the analytics data, with a spend cap that refuses before it costs anything |
| [`adr/0030`](adr/0030-what-store-data-may-reach-a-model-provider.md) | What store data may reach a model provider, decided before the Feature was sellable |

`loyalty-rewards` became 1.1.0 and moved its expiry onto a declared worker, as
phase 14 said it would.

---

## 2. Workers

The schedule is a closed list — `hourly`, `daily`, `weekly` — rather than a cron
expression. A cron string is a parser, a timezone question and a support surface,
and every scheduled job a Feature has actually wanted is one of those three.
Widening the list later is additive; narrowing it after somebody has shipped a
cron string is not. The **word** travels and the store decides what it means,
because the store is the only party that knows its own timezone.

Validated hard at publish, because a worker is code KNIGHT causes a store to run
on a timer with nobody watching. A malformed entrypoint is a job that fails
silently every hour for as long as the Feature is installed. Two workers of one
name are refused for the same reason: a store records the last run per name, so
the second would overwrite the first and one of them would never be seen as due.

The runner is built around the three things that go wrong on a timer:

- **One misbehaving worker loses its own run and nothing else.** A Feature with a
  bad import must not cost a store its other scheduled jobs, and must never cost
  it the shop.
- **Every run is recorded, including the failures.** "Did the nightly job run" is
  the first question anybody asks.
- **A failure does not move the next run forward.** A job that has been failing
  for three days is still due, not quietly rescheduled.

A corrupt run history is refused rather than read as empty. Reading it as empty
would run every worker at once — and for a worker that sends email, that is a
store mailing its whole customer list twice.

Driven end to end against a real store:

```
  ran        ai-reports/daily-report            [{'covers': '2026-08-25', 'findings': 2, 'narrated': True}]
  ran        loyalty-rewards/expire-points      0
  ran        marketing-automation/run-campaigns [RunReport(campaign='welcome', considered=12, sent=1, suppressed=1, no_contact=10)]
  3 ran, 0 failed.
```

Three workers, three Features, all from declarations delivered with their
installs. Immediately afterwards the runner reports "Nothing is due", and
`--force` runs them anyway for an operator who needs one now.

---

## 3. Marketing automation

The first Feature whose dangerous failure is not losing data — it is **sending**.
Every default is chosen accordingly, and three tables exist purely to stop it:

- **`Contact`** holds the address and *when consent was given*. No consent, no
  send. The store registers contacts explicitly, because consent is a fact the
  store collected; a marketing package that inferred it from the existence of a
  customer would be inventing permission nobody gave.
- **`Suppression`** is keyed on the **address**, not the customer. Somebody who
  unsubscribes has withdrawn permission for that address, and a store that later
  registers it under a different customer id does not get a fresh start.
- **`Send`** is unique per campaign and subject. That constraint — not a check a
  second run would also pass — is what makes a campaign unable to mail the same
  person twice.

The send path is **decide, record, then send**. The row is written before the
provider is called; sending first and recording after is how a crash between the
two becomes a duplicate message.

A real run, against real segment data:

| Recipient | Outcome |
|---|---|
| `ann`, consented | Sent, with a provider message id |
| `ben`, consented then suppressed | Suppressed — not sent |
| `cara`, no contact record | NoContact — not sent |
| all three, run again | `already_sent=3`, nothing re-sent |

The dry run reported the same three numbers and wrote nothing.

**Installs switched off.** All four campaigns are seeded inactive and the
provider defaults to `recording`, which does everything except deliver. A
marketing package that began mailing the moment it was installed would be the
worst default in this catalogue.

---

## 4. AI reports, and the cost control

The commercial value is **automated business interpretation**, not "AI", and that
framing decides the architecture:

- **Findings are computed, not generated.** Deltas against a baseline, average
  order value, revenue concentration — all arithmetic, all deterministic, all
  carrying the numbers they were drawn from so a merchant who disagrees can
  check. A number a merchant acts on is never something a model produced.
- **Prose is generated, and optional.** If the provider is absent, over budget or
  broken, the findings still stand and the report says so.

That split is what makes the cost control meaningful: something concrete is being
bought, and it can be refused without the Feature stopping working.

A real report, from real events — four baseline days of ten orders, then a day of
four:

```
[Urgent] Orders are down 60% on the recent average (4 against 10.00).
[Urgent] Revenue is down 60% on the recent average (400.00 against 1000.00).

The day of 2026-08-25 needs attention.
```

And the budget, exercised:

| Checked | Result |
|---|---|
| Local provider | Never charged; report narrated, `tokensUsed: 0` |
| API provider with headroom | Priced at 640 tokens before the call |
| API provider over cap | Refused **before** the provider was called |
| Findings after a refusal | Still there — the report exists either way |
| Window rollover | Reset on read, not by a job |

The refusal is asked before the provider, not after. A limit that costs money to
enforce is not a limit.

**What may leave the store** is settled by
[`adr/0030`](adr/0030-what-store-data-may-reach-a-model-provider.md): an
allow-list of aggregate fields, built by allow-list rather than deny-list because
a deny-list is one new field away from leaking. The concentration finding says
"62% of the period's revenue came from a single customer" and deliberately does
not say which — not because redaction would strip it, but because a finding
carrying a customer reference would be an identifier sitting in a record designed
to be sent onward.

---

## 5. What verifying it found

**A test file that could not be imported on a base store.** `test_ai_reports.py`
annotated a helper as `report: Report` — a type that only exists when the Feature
is installed — and Python evaluates annotations at function-definition time. The
module failed to load rather than skipping, and the whole suite errored.

It was caught by running the suite **with no Features installed**, which is
exactly what that configuration is for and the only run that could have found it.
Fixed with `from __future__ import annotations`, which makes the whole class of
mistake impossible in that file.

**An unused statistical helper that failed its own motivating example.** An
`outliers()` function used a z-score over the baseline. On its own example —
`[10, 11, 10, 9, 100]` — the single outlier inflated the standard deviation
enough to hide itself: the deviation was 72 and the threshold was exactly 72.0.
That is a real weakness of z-score on small samples, and the function was not
called by anything. It was removed rather than tuned: shipping dead statistical
code that fails on the case it exists for is worse than not shipping it. Anomaly
detection should arrive with a finding that uses it.

**A `DateField` defaulted to `timezone.now`.** `Budget.window_started_on` is a
date and `timezone.now` returns a datetime. Django coerces it on save, so the
mistake survived a round trip and surfaced only in the API response, where the
usage window printed a full timestamp. Now `timezone.localdate`.

**The packaging tool could not read a `workers:` block** — and this is the third
phase in a row where a hand-rolled YAML fallback has been the thing that broke.
`knight_package.py` keeps its own small reader for build containers that have
nothing but the standard library. It handled *inline* sequence items, because
`dependencies.features` is written that way, and raised on block-style ones:

```
TypeError: list indices must be integers or slices, not str
```

Raising rather than guessing was correct — the docstring says so, and this reader
produces signed artifacts. It reads block-style items now.

**And fixing that exposed a worse one, which had been there since phase 3.5.**
The inline-map reader split on every comma, so a dependency written
`{ slug: analytics-core, version: ">=1.0.0,<2.0.0" }` parsed as a slug, a
`version` of `">=1.0.0`, and a third key called `<2.0.0"`.

Nothing had noticed, because the tool reads a manifest for the slug, the version
and the package directory and does not resolve dependencies itself. So it built a
**correct artifact from a manifest it had misread** — which is the worst shape a
parser bug can take, and the reason the split is quote-aware now. Every
dependency range in the catalogue was affected; none of the artifacts were.

---

## 6. Repeating it

Database up, from the repository root:

```bash
docker compose -f infrastructure/docker/docker-compose.yml up -d
```

In `stores/reference-store`, with the **Python 3.12** virtualenv:

```bash
python -m pip install ../../features/knight-feature-marketing-automation ../../features/knight-feature-ai-reports
```

```bash
python manage.py knight_install_local ../../features/knight-feature-*/
```

```bash
python manage.py migrate
```

Put the runner on cron, as often as the shortest schedule any Feature uses:

```bash
*/15 * * * * cd /srv/store && .venv/bin/python manage.py knight_run_workers
```

By hand, to see what is due without running it:

```bash
python manage.py knight_run_workers --dry-run
```

Then the surfaces:

```bash
curl "http://localhost:8010/ai-reports/"
```

```bash
curl "http://localhost:8010/ai-reports/usage/"
```

```bash
curl "http://localhost:8010/marketing/"
```

And the suites:

```bash
REQUIRE_FEATURE_TESTS=1 python manage.py test
```

---

## 7. Test results

| Suite | Result |
|---|---|
| Store, all 10 Features installed, `REQUIRE_FEATURE_TESTS=1` | **479 passed**, 0 skipped (367 before) |
| Store, no Features installed at all | **479 passed**, 279 skipped |
| Backend unit | **609 passed** (595 before) |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | 153 passed |

The 112 new store tests are the two Features plus 22 on the worker runner, and
14 of the new backend unit tests are manifest validation of the `workers:` block.

---

## 8. What is deliberately not covered

**Neither Feature calls a vendor.** `marketing-automation`'s `api` provider and
`ai-reports`' `api` provider both read their named secret, validate it, and then
refuse with a clear message. Wiring a real vendor is an integration against a
real account under a real agreement; inventing one here would mean shipping code
nobody has watched work. The path up to and including the refusal is real and
tested — the secret is delivered over the install channel, read through
`config.secret()`, never returned by the configuration endpoint, and never
present in an error message.

**Which provider, under what agreement, in which jurisdiction** is not decided.
That is a commercial and legal question for whoever operates KNIGHT, and
[`adr/0030`](adr/0030-what-store-data-may-reach-a-model-provider.md) says so
rather than pretending otherwise. It has to be answered before either `api`
provider is wired up.

**Abandoned-cart campaigns need an event the base store does not emit.** The
trigger reads `cart.abandoned` from the analytics stream. A store that wants that
campaign has to emit it; the Feature names the event rather than inventing one.
The same is true of `order.placed`, which the base store does emit.

**Concurrency is still argued rather than proven**, carried from phase 14. The
worker runner's isolation and the send constraint are the right shapes and every
idempotency path has a test, but nothing runs two transactions at once to watch
them contend.

**None of this is reachable from the dashboard.** Campaigns, suppressions,
budgets and reports are all store-side, and KNIGHT is not a store's business
backend — but it means a merchant edits a campaign template from a shell.
