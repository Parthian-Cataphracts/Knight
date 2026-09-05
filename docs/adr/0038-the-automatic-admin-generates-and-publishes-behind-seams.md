# 0038 — The Automatic Admin generates and publishes behind provider seams

**Status:** accepted

Relates to [`0037`](0037-composed-pricing-and-sub-features.md) (the Automatic
Admin is a composed Feature), [`0030`](0030-what-store-data-may-reach-a-model-provider.md)
(what store data may reach a model provider), [`0033`](0033-api-driven-features.md)
(a Feature can be an external service), and [`0034`](0034-a-shared-secret-has-a-lifetime.md)
(per-store secret delivery). Follows the seam-and-simulated-adapter pattern that
[`docs/self-service-saas-plan.md`](../self-service-saas-plan.md) §11 established
for payments (`IPlatformPaymentProvider` / `SimulatedPaymentProvider`) and
infrastructure (`SimulatedInfrastructureAdapter`).

## Context

The Automatic Admin behaves like a real store admin, automatically: the customer
gives it a topic, and it generates content — image, caption, story, short video —
and publishes it to the channels the customer connected (Telegram, Instagram,
Divar, Basalam), replies to their customers, and works their visibility up. The
value the owner is selling is not "an AI writes a caption" — that is becoming a
commodity — it is that the admin is **connected to this store and its channels**
and reports for approval like a real employee.

Two facts make this hard to build the obvious way, and one product decision
shapes it:

- **No external provider is available yet.** The AI model and key are the owner's
  decision, still open. Each channel has its own API, credentials and
  authorisation flow, and several — Instagram and WhatsApp via Meta's Graph API —
  need a Business account and app review and are sanction/IP-blocked from Iran.
  Telegram's Bot API, by contrast, is reachable and simple.
- **Getting the account is the hard part, not the code.** If publishing to a
  channel is written against a narrow seam, the real integration is a later
  drop-in, one provider at a time, and everything around it can be built and
  tested now.
- **A wrong post or a wrong reply is the merchant's liability.** The owner chose:
  autonomy is a **per-customer setting that defaults to draft-then-approve**, not
  fully automatic; and the visibility capability is the **ToS-safe version** —
  scheduling, content and listing optimisation, and managing paid promotion
  (`نردبان`) — with **no automated following or engagement**, which violates
  platform terms and gets the merchant's account banned.

## Decision

**Generate and publish behind two seams, with simulated adapters, exactly as
payments and infrastructure already do.**

1. **`IContentGenerator`** — given a brief (topic + the kind of content), returns
   generated content. `SimulatedContentGenerator` returns deterministic
   placeholder content, so the whole journey runs locally with no model and no
   key. A real generator — the owner's chosen model, bound by
   [`0030`](0030-what-store-data-may-reach-a-model-provider.md) on what store data
   may reach it — is a drop-in adapter and nothing else.

2. **`IChannelPublisher`**, resolved through a per-channel registry (the shape of
   `IPlatformPaymentProviderRegistry`) — given content and a connected channel,
   publishes it and returns a result. `SimulatedChannelPublisher` records the
   "publish" to an inspectable sink. A real publisher per channel is a drop-in
   that gets its credentials through the per-store secret delivery built in phases
   24 and 31 ([`0034`](0034-a-shared-secret-has-a-lifetime.md)). Channels are
   sequenced **Telegram first, then Divar and Basalam, Meta last**, because Meta
   API access from Iran is the blocker, not the publishing code.

3. **An orchestrator** sits above both — the "give it a topic and it does
   everything" brain. It takes a brief, the customer's connected channels and
   their autonomy mode; generates content for the generation parts the customer is
   **entitled** to; then, per the autonomy mode, either records a **draft awaiting
   approval** (the default) or publishes immediately; and records the outcome as a
   report. Generation parts gate generation and channel parts gate publishing,
   enforced by the phase-32B entitlement gating — the admin only does what the
   customer bought.

4. **Autonomy is a per-customer setting defaulting to `ApprovalRequired`.** A
   draft never leaves the system until it is approved; full-auto is an opt-in the
   customer sets deliberately. The default is safe because the downside — a wrong
   price, a wrong reply — lands on the merchant.

5. **Visibility is the safe version only.** Scheduling (publish at a computed good
   time), content and listing optimisation, and paid-promotion management are in.
   Automated following/liking of other accounts is **out**: it breaks platform
   terms and endangers the merchant's account, and KNIGHT will not ship a
   capability whose success condition is "the account is not banned yet."

The two seams, the simulated adapters and the orchestrator ship in this phase.
Real adapters arrive later, one provider at a time, changing only the adapter —
the entitlement gating, the drafts-and-approval flow, the reporting and the
delivery drill are built and tested now.

## Alternatives considered

- **Write directly against a first channel's SDK now (e.g. Instagram).** Rejected:
  it couples the whole feature to the one channel that is hardest to reach from
  Iran, and there is no key to test against. The seam lets Telegram be the first
  real adapter while the rest stay simulated.
- **Ship fully automatic by default because the owner said "like a real admin,
  automatically."** Rejected as the *default*: kept as an opt-in. A first wrong
  post that goes out with nobody having seen it is a worse first impression than a
  draft that waited.
- **Include auto-follow / auto-engagement to grow reach.** Rejected outright: it
  is against platform terms, requires unofficial automation, and its foreseeable
  outcome is the customer's account being restricted or banned.

## Consequences

- A new module owns the two seams, the simulated adapters, the per-channel
  registry, the orchestrator, the per-customer autonomy setting, and the content
  job / draft / report state, with its own persistence and endpoints. A delivery
  drill exercises topic → generate → (approve) → publish (simulated) → report, the
  bar every catalogue Feature clears.
- The real content generator, when it lands, obeys
  [`0030`](0030-what-store-data-may-reach-a-model-provider.md). The real channel
  publishers get credentials through per-store secret delivery and are sequenced
  by how reachable each channel's API is.
- Deeper "commercial" suggestions — *push this slow-moving product* — need the
  store's own sales data, which the control plane does not hold and by rule never
  touches (`docs/architecture.md`). That depth is an in-process capability
  deployed into the store, like `advanced-search`, and is out of this phase's
  scope; v1 gives content-side suggestions from the generation seam.

## What this does not decide

It does not choose the AI model or provider (owner, deferred). It does not specify
any channel's API beyond the seam shape — each real channel is an owner-supplied
integration. It does not decide usage-based pricing for generation
([`0037`](0037-composed-pricing-and-sub-features.md) deferred it); a per-plan
generation cap is a pricing detail, not an architecture decision.
