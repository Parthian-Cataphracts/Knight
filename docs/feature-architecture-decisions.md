# Which Features are services, and which are not

Sixteen sellable Features, and a recorded decision for each: **service**,
**in-process**, or **arguable, and here is the argument**.

The split is the deliverable, not a step towards converting everything. A pivot
that moved the whole catalogue and wrote down nothing would leave the next
person guessing which choices were considered and which were momentum — and the
in-process path is not a legacy to apologise for. It is the only way to be
inside the store's transaction, and three of these Features need to be
([`adr/0033`](adr/0033-api-driven-features.md), [`adr/0024`](adr/0024-base-store-versus-optional-feature.md)).

## The test

One question decides most of it:

> **Does this Feature have to be inside the store's own database transaction?**

There is no way to be inside a transaction over HTTP. A service can be told what
happened; it cannot take part in deciding it. So anything that must refuse a
checkout, hold a row against a concurrent one, or be consistent with an order at
the instant the order is written stays in-process — and everything else is
argued on operational grounds: who holds the vendor credential, whose schedule
the deploy runs on, and whether the logic is the same for every store.

Two secondary tests break the ties:

- **Whose credential is it?** A Feature that calls a vendor holds a credential
  the merchant is not equipped to protect, and a store that never sees one
  cannot leak one.
- **Whose deploy is it?** An in-process Feature is fixed when every store takes
  the new version; a service is fixed once, by the people who wrote it.

---

## The table

| Feature | Decision | Why |
|---|---|---|
| `subscriptions` | **Service** — *done, 2.1.0* | The billing clock, the retry policy and the provider credential are identical for every shop, and none of it belongs in a shop's transaction. It bills, then tells the store what to make. Delivered as a service since phase 22 |
| `external-marketplaces` | **Should be a service** | The clearest remaining case. It is a queue in front of somebody else's API, holding OAuth tokens for four kinds of partner, with retry policy and idempotency that are the same everywhere. The tokens are the argument on their own: a credential that can place orders in a merchant's name should not sit in a store somebody else operates |
| `marketing-automation` | **Should be a service** | Campaign logic is identical per store, and it holds an e-mail or SMS provider credential that can send in the merchant's name. It already consumes events rather than participating in them — welcome, post-purchase, abandoned-cart and win-back are all reactions to something that already happened |
| `ai-reports` | **Should be a service** | Reads analytics rollups and writes prose. Nothing it does is in a transaction, its findings are the same arithmetic for every shop, and if a model provider is ever wired in, that credential must not be in a store ([`adr/0030`](adr/0030-what-store-data-may-reach-a-model-provider.md)) |
| `advanced-inventory` | **In-process** | The reason the in-process path exists. Reserving stock during checkout has to be inside the transaction that writes the order, or two shoppers buy the last item. An HTTP reservation is a reservation somebody can lose a race against |
| `advanced-promotions` | **In-process** | It changes what an order costs, while the order is being priced. A service could be asked what a discount is worth, and the store would then have to trust an answer it could not make atomic with the checkout it is committing |
| `gift-cards` | **In-process** | A ledger the checkout spends from. Redeeming has to be part of the transaction that takes the payment, or a card is spent twice by two tabs |
| `loyalty-rewards` | **In-process** | Same shape as gift cards: points are earned and spent against an order as it is written. Earning could be a reaction; spending cannot |
| `restaurant-operations` | **In-process** | Table sessions, kitchen tickets and slot bookings are the store's own operational state, changing many times a minute inside the shop with no third party involved. A network hop per ticket transition would be a kitchen screen that stalls when a link does |
| `multi-location` | **In-process** | Order routing decides where an order goes as it is placed, and per-branch menu exceptions are read on every catalogue request. Both are hot paths inside the store's own data |
| `advanced-search` | **In-process** | It is a PostgreSQL index in the store's own database, over the store's own catalogue. As a service it would need a copy of the catalogue, which is a synchronisation problem invented to avoid a library |
| `analytics-core` | **In-process** | It owns the event table other Features read, and it is written to on the store's hot paths. Moving it would put a network hop in front of every recorded event and leave the Features that read it querying somebody else |
| `analytics-reports` | **Arguable → in-process for now** | Nothing it does is transactional, so it *could* be a service — but it reads `analytics-core`'s rollups directly, and a service would have to be given that data. It moves if and when `analytics-core` does, and not before |
| `customer-segmentation` | **Arguable → in-process for now** | A scheduled recomputation over the analytics event stream: a natural service, held back by the same dependency as `analytics-reports`. The segments are also read by `marketing-automation`, so the two should move together or neither should |
| `reviews-ratings` | **Arguable → in-process** | Reviews are not transactional and moderation is generic, which argues for a service. Against it: reviews are rendered on every product page, so a service puts a third party on the storefront's read path, and the data is the merchant's own content rather than anything a vendor holds. Staying is the cheaper correctness |
| `log-shipping` | **Neither — it is KNIGHT's** | The one capability with no package at all. The work is done by KNIGHT's own ingest endpoint, which refuses a batch from an unentitled customer. Entitled, enforced server-side, audited, and nothing to install ([`feature-catalog.md`](feature-catalog.md)) |

**Four should be services; one of them is.** Eleven stay in-process, and eight of
those are the transaction argument rather than a preference.

## What the three that should move are waiting for

Not a decision — this one is made. Each is a phase-22-sized piece of work:

- a Django service with its own database and its own deployment;
- the manifest rewritten as `architecture: external_service`, with the events it
  needs and the routes the store forwards;
- a store command where the Feature used to have direct access, for anything the
  service may not do itself (the shape `subscriptions` uses for orders);
- and a drill step, because "installed" and "working" are different claims and
  only the drill tells them apart.

`subscriptions` took two phases to move and found six defects doing it, every
one of which was invisible until two processes had to agree. Moving three more
in one pass, without that scrutiny each, would be the same mistake at three
times the size.

## What would change these decisions

Written down so that a future argument starts from the evidence rather than from
scratch:

- **A store-side transaction protocol.** If a store could enlist a service in a
  two-phase commit — or, more realistically, if the store adopted a reservation
  protocol that tolerated an asynchronous confirmation — then `advanced-inventory`
  and `gift-cards` would be arguable rather than settled.
- **`analytics-core` becoming a service.** It is the dependency under three of
  the "arguable" rows. If the event table moved, `analytics-reports`,
  `customer-segmentation` and eventually `ai-reports` would follow it in one
  coherent move rather than three awkward ones.
- **A second store on a runtime that has no in-process path.** A Feature that
  must reach a .NET store today has to be written twice, or be a service. That
  is a commercial reason, not a technical one, and it is the reason most likely
  to override the table above.
