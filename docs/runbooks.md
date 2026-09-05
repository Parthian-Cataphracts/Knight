# Runbooks

One entry per alert KNIGHT can raise. **An alert without a runbook is a page at
three in the morning that begins with reading source code**, which is why this
file is release-blocking for any new rule: a rule that raises something nobody
knows how to act on has made the night worse rather than better.

Every entry has the same four parts, in the order somebody actually needs them:
what the alert means, what to look at first, what to do, and when it is safe to
ignore. The last one matters as much as the others — an alert that can never be
ignored is one people learn to ignore.

The rules themselves are in `ObservabilityRules`; the sweep that raises the ones
which need a timer is `ObservabilityRuleEvaluator`, and it runs on the interval
in `Observability:SweepInterval`.

---

## `delivery.dead_lettered` — critical

**What it means.** A store used every attempt at delivering one of its events to
a Feature's service and gave up. Seven attempts over roughly twelve hours. The
event will never be delivered: this is the record that a Feature a customer pays
for did not hear something that happened in their shop.

**Look at first.** The alert names the store and the Feature. Then, on the store:

```bash
python manage.py knight_deliver --dead-letters
```

That lists what was dropped, with the last status and error for each. A 502 or a
timeout on all seven attempts is a service that was down; a 400 on all seven is a
service that is up and rejecting the event, which is a bug in the Feature and not
in the delivery.

**What to do.** Get the service answering, then replay: a dead letter is kept
precisely so it can be. Nothing replays automatically, on purpose — twelve hours
of events arriving at once, unannounced, is its own incident. Decide whether the
Feature can take them, and if it can:

```bash
python manage.py knight_deliver --replay <delivery id>
```

If the service was rejecting them, the events are still lost after the fix and
the customer needs telling. That is the honest answer and there is no command
for it.

**Safe to ignore when.** The Feature was being uninstalled, or the entitlement
was withdrawn during the window — the store stops forwarding, and events already
queued run out their attempts against a service that is no longer any of its
business. Check the installation's state before assuming anything is wrong.

---

## `service.unreachable` — warning

**What it means.** A store forwarded a shopper's request to a Feature's service
and got nothing back, or had no shared secret to sign it with. The shopper saw a
page. The store is fine; something at the other end is not.

**Look at first.** The Feature's own service: is it up, and does its `/healthz`
answer? Then the store's own report, which carries the path and the method.

If the message is about a missing secret rather than a timeout, this is not an
outage — it is a configuration that never arrived. Issue the store's credential
again:

```
POST /api/v1/installations/service-secret { storeId, featureId }
```

**What to do.** Nothing on the KNIGHT side fixes a service that is down; the
Feature's operator does. What KNIGHT can do is tell whether it is one store or
all of them: the same rule raises per store, so several open alerts naming one
Feature is that Feature's outage, and one alert naming one store is usually that
store's network.

**Safe to ignore when.** A single occurrence during a deploy of that service.
The alert closes on its own once the sweep stops seeing new reports.

---

## `job.stuck` — warning

**What it means.** An agent claimed an installation job and never reported
again. Past the claim timeout, the job is presumed dead rather than slow.

**Look at first.** The job's steps on the jobs screen: the last step it reported
is where the store stopped. A `migrate` that never reported is the one to be
careful with — the store may have applied half a schema change.

**What to do.** Confirm the store is alive (its heartbeat), then let the sweep
requeue the job. If it stopped at `migrate`, check the store's own migration
state before requeueing: rerunning is safe, because every step is written to be,
but knowing which half ran is worth thirty seconds.

**Safe to ignore when.** Never entirely — but a store that was restarted
mid-install produces exactly this, and the requeue is the whole fix.

---

## `feature.install.failed` — critical

**What it means.** An installation job ended in failure. The store said which
step and why.

**Look at first.** The failure code on the job. `signature.invalid` is a signing
key mismatch and not a store problem; `fetch.oversized` and `fetch.failed` are
the artifact store; anything under `install.` is the Feature's own package.

**What to do.** Fix the cause and reinstall. A failed install leaves the previous
version serving, which is why this is critical but not urgent-at-night unless it
is a first install.

**Safe to ignore when.** The same Feature failed for the same reason on many
stores at once — that is one problem, not many, and the other alerts are
duplicates of this one.

---

## `feature.entitled_not_installed` — warning

**What it means.** A customer is paying for something that is not on their
store, past the grace period. The commercial promise and the running system have
drifted apart.

**Look at first.** Whether an install job exists at all. None means nothing ever
queued it; a failed one means this alert is a symptom of
`feature.install.failed`.

**What to do.** Queue the install. If it was queued and failed, work that alert
instead.

**Safe to ignore when.** The store is suspended or archived — it is not supposed
to be serving, and nothing should be delivered to it.

---

## `feature.drift` — warning

**What it means.** The store reports running a version other than the one KNIGHT
installed. Somebody changed the store by hand, or an install half-succeeded.

**Look at first.** Which direction the drift goes. A store on an *older* version
than KNIGHT recorded is usually a rollback nobody told KNIGHT about; a *newer*
one is somebody installing a package by hand.

**What to do.** Reinstall the version KNIGHT intends, which makes the two agree
again. Find out how it drifted before doing it a second time.

**Safe to ignore when.** During a deliberate rollback, until it settles.

---

## `backup.overdue` — critical

**What it means.** No successful backup has been reported for a store in longer
than the configured window. This alerts on an *absence*, which is why it needs
the sweep: a backup job that was switched off says nothing at all.

**Look at first.** Whether backups are configured for that store, and when the
last successful one was.

**What to do.** Run one, and find out why the schedule stopped. A store with no
recent backup is the one failure on this list that cannot be recovered from
after the fact.

**Safe to ignore when.** Never. A development store that genuinely does not need
backups should have them switched off, which stops the rule considering it.

---

## `errors.spike` — warning

**What it means.** One error group's rate is far above its own recent baseline —
not above a global threshold, which is what makes it meaningful for an endpoint
that always throws twice an hour.

**Look at first.** The group's own screen: the newest sample, and whether the
spike starts at a deploy.

**What to do.** Ordinary triage. Acknowledging or resolving the group stops it
alerting, which is the intended way to say "known".

**Safe to ignore when.** A load test, or a synthetic run. Say so by
acknowledging the group rather than by ignoring the alert.

---

## `errors.regression` — warning

**What it means.** A group somebody had resolved has started happening again.

**Look at first.** What changed since it was resolved. A regression alert is
almost always a deploy.

**What to do.** Reopen the group and treat it as new. It is worth finding out
whether the original fix was wrong or was reverted; those need different
answers.

**Safe to ignore when.** The resolution was optimistic and the group is known to
recur — in which case ignore the *group*, not this alert.

---

## `server.offline` — critical

**What it means.** A machine in the fleet stopped reporting: no heartbeat from
its agent for the configured number of missed intervals, so the sweep has marked
the server offline. Every store on that machine is affected — delivery to them
stalls until an agent reports again — whether or not their own probes have
noticed yet.

**Look at first.** Whether the host itself is down or only its agent — try
reaching the machine. The monitoring screen names the server and when it was last
seen; the stores hosted on it are the blast radius.

**What to do.** If the host is down, bring it back. If the host is up and only the
agent stopped, restart the agent on it (`knightctl restart` on the machine).
Nothing on that server takes delivery until an agent reports, so a job queued for
one of its stores waits rather than fails.

**Safe to ignore when.** A planned reboot or maintenance window on that host. The
alert closes on its own once the agent reports and the next sweep sees the server
healthy again.

---

## `backup.failed` — critical

**What it means.** A store's backup job ran and failed. This is the loud twin of
`backup.overdue`: overdue is a backup that quietly stopped running, and this is
one that tried and did not produce a usable dump. The store's recoverable point
is still the last *successful* backup, not this attempt.

**Look at first.** The store's own report for why it failed — a full disk, a
locked table, a credential — and the timestamp of the last successful backup,
which is how far back the store can currently be restored.

**What to do.** Fix the cause and run a backup by hand; a success resolves this
alert on its own (and the overdue one with it). Until one succeeds, treat any
risky operation on that store as unrecoverable past the last good dump.

**Safe to ignore when.** A single transient that the next scheduled run recovers
from — but confirm the next run actually succeeded rather than assuming it did.
