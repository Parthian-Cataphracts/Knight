# 0030 — What store data may reach a model provider

- Status: **Accepted**
- Date: 2026-08-26

## Context

`ai-reports` is the first Feature that can send a customer's business data to a
third party. Phase 15's exit criteria required this decided and written down
before it was sellable, not after
([`../../TODO.md`](../../TODO.md), and [`phase-15-verification.md`](../phase-15-verification.md)).

The question is narrow and the answer has to be too. A store's analytics event
stream contains, or could contain:

- aggregate counts and sums — orders per day, revenue, average order value;
- **customer references** — the `subject` string `analytics-core` records against
  each event;
- **event payloads** — whatever the store chose to put in them, which is a
  document field and therefore anything at all;
- derived facts about individuals — that a particular subject accounts for most
  of a day's revenue.

A shop in the EU using this Feature is a data controller sending personal data to
a processor it did not choose. "Send the events to a model and ask for a summary"
is the obvious implementation and it is not one KNIGHT can offer.

## Decision

**Only aggregates leave the store, and only aggregates a KNIGHT-authored
function computed.**

Concretely, the payload a model provider may receive is exactly what
`providers.redact()` produces: for each finding, a code, a severity, the sentence
KNIGHT's own arithmetic wrote, and a small allow-listed set of numeric fields.
Nothing else in the process is permitted to reach a provider.

Three rules follow, and each one is a design constraint rather than a guideline.

### 1. Findings are computed, prose is generated

The arithmetic — deltas against a baseline, average order value, revenue
concentration — runs
locally in `analysis.py`. It is deterministic, free, and auditable: every finding
carries the numbers it was drawn from, so a merchant who disagrees can check it.

The model's only job is to turn those findings into a paragraph. **A number a
merchant acts on is never something a model produced.** That is a correctness
decision as much as a privacy one: a hallucinated 12% is worse than no report.

It also means the default provider can be `local`, which sends nothing at all
and still produces something worth reading. A store that never enables an
external provider loses register, not substance.

### 2. The payload is an allow-list, never a deny-list

`redact()` names the fields that may travel. A deny-list is one new field away
from leaking, and the new field is always added by somebody who is not thinking
about this decision.

The rule extends into the analysis: a finding **may not carry a customer
reference at all**. The concentration finding says "62% of the period's revenue
came from a single customer" and deliberately does not say which — not because
`redact()` would strip it, but because a finding that carried it would be a
customer identifier sitting in a record that is designed to be sent onward.

### 3. Event payloads never travel

The `payload` field on an analytics event is a document, and a store may put
anything in it — a delivery note, an address, something a shopper typed. Nothing
reads it for narration purposes except `analytics-core`'s own `value` extraction,
which produces a sum.

## Consequences

**Positive** — a merchant can answer what leaves their shop, and the answer is a
short list they can read. The Feature works with no provider configured, so
enabling one is a deliberate act with a visible cost rather than the price of the
Feature functioning at all. And because the substance is computed locally, a
provider being slow, down, over budget or newly untrusted degrades the report
rather than breaking it.

**Negative** — the narration is worse than it could be. A model given the raw
event stream could notice things the arithmetic does not, and this decision
forecloses that. That is accepted: the class of thing it could notice is not
worth the class of thing it could leak, and a merchant who wants it can export
their own data and ask a model themselves — as their own controller, with their
own agreement.

**Also** — this makes `requiresDedicatedInfrastructure` about cost and isolation
rather than about data. The Feature does not need a dedicated machine to keep
data in; it needs one because per-customer spend on a shared host cannot be
bounded, and that is a different argument recorded in the catalogue
([`feature-catalog.md`](../feature-catalog.md)).

**Not decided here** — which provider, under what agreement, in which
jurisdiction. That is a commercial and legal question for whoever operates
KNIGHT, and it has to be answered before the `api` provider is wired to a real
vendor. The code refuses clearly until then, which is the honest state of it
rather than a stub pretending to work.
