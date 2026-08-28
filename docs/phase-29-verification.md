# Phase 29 — the one item that was not a decision

Phase 29 is the release decision, and it is the product owner's. Its exit
criterion is the six conditions in [`roadmap.md`](roadmap.md) §2 being true; its
gate is stated in the TODO as, exactly, **"yours"**.

Four of its five items are things nobody here can do:

| Item | Whose |
|---|---|
| The external security review of the code-delivery path | needs a firm engaged. **Longest lead time on the whole roadmap**; R16 stays open until the report exists |
| The eleven architecture-validation questions from phase 0 | the product owner's answers, not findings anybody can produce |
| The restore drill against production-shaped data | needs a production database. It has run in CI on every push since phase 9, against a seeded one |
| A decision on the in-process path — deprecated with a date, or kept as the transactional option | the call itself. Phase 28's [decision table](feature-architecture-decisions.md) is the input, and it argues for keeping it |

The fifth was buildable, and it was built.

---

## Domain verification, both halves

**What was wrong.** The model has had two verification methods since phase 3 and
one implementation. `DnsTextRecord` was an enum value, a record name shown to
operators, and nothing behind it. A store whose domain had no HTTP surface yet —
bought this morning, or pointed at a machine still being provisioned — had no way
to prove it owned anything, which is exactly the store that most needs to.

**What was built.** A TXT lookup at `_knight-verification.<domain>`, in about a
hundred lines of DNS and no new dependency. HTTP is tried first because it is
the method an operator can satisfy in a minute with a file; DNS second because it
is the only one available before there is a server. A store proves itself by
either.

Four decisions in it are worth stating, because each is a place this kind of
code usually goes wrong:

- **The answer is compared, never fetched.** Whatever is published at that name
  is somebody else's string; the only thing done with it is a fixed-time
  comparison against a token KNIGHT issued. That is what keeps a hostile TXT
  record from becoming a request KNIGHT makes on somebody's behalf.
- **Exact after trimming.** A record that merely *contains* the token does not
  verify. An error page echoing a request — or a TXT record quoting one — is not
  evidence that anybody chose to publish anything.
- **A compression pointer ends a name.** A pointer that points at itself is the
  classic way to hang a DNS parser, and hanging one inside a control plane is a
  denial of service anybody can post.
- **Truncation is reported as "not published" rather than retried over TCP.** A
  verification token is forty-odd characters; an answer too big for a datagram
  means something else is at that name, and fetching it is not this code's
  business.

**Where a token beside other records is normal.** Any TXT record at the name may
carry it. Publishing a second record next to SPF or an ACME challenge is how
this is done on a domain already in use, and refusing because the first record
was somebody else's would be refusing the ordinary case.

## What verifying it found

**A DNS query's tail is five bytes, not four.** The first version of the test
asserted the record type at `query[^4]` and read the class byte instead. It
failed, which is the test doing its job — but it is worth noting that the
assertion was wrong rather than the encoder, and the encoder had been right all
along.

## How to test it

```bash
cd backend
dotnet test tests/Knight.UnitTests --filter FullyQualifiedName~DomainVerificationTests
```

Thirteen tests: seven on the wire format — an answer to somebody else's
question, a truncated one, an error rcode, a long value split across strings, a
self-referential pointer — and six on the judgement, including another store's
token, a record that merely contains the token, and which refusal is reported
when neither method worked.

By hand, against a domain you control:

```
POST /api/v1/stores/{id}/domain-verification          # issues the token
# publish it:  _knight-verification.shop.example.com. IN TXT "knight-verify-…"
POST /api/v1/stores/{id}/domain-verification/verify
```

Expect `{"verified": true, "method": "DnsTextRecord"}`.

## The numbers

| | |
|---|---|
| KNIGHT backend | **704 unit** (thirteen new), **13 architecture**, **174 integration** |

## What phase 29 is still waiting for

Everything else on its list, and none of it is code:

- the security review, which has a scope and briefing pack ready in
  [`security/external-review-scope.md`](security/external-review-scope.md) and
  needs a firm;
- eleven answers in [`risks.md`](risks.md) §3;
- a production database to run the restore drill against;
- and the call on the in-process path.

**Nothing in the delivery path gates on domain verification, and that is still
true.** A store can be installed into while its domain is `Pending`.
`RequireDomainVerification` exists on the handshake and is off by default;
turning it on is a product decision with a real consequence — every store whose
domain is not verified stops being able to hand shake — and it belongs with the
release decision rather than in front of it.
