"""
Running the scheduled jobs installed Features declare.

A Feature says in its manifest that something must happen hourly, daily or
weekly; KNIGHT delivers that declaration with the install; and this is what
makes it true on the store. Installing a Feature installs its schedule, which is
the whole point — a worker that has to be wired up by hand on every store is a
worker that does nothing on the stores where somebody forgot.

Three rules shape everything here:

- **One misbehaving worker must not stop the others.** A Feature with a bad
  import or a raising job loses its own run and nothing else. The store keeps
  selling.
- **Every run is recorded, including the failures.** "Did the nightly job run"
  is the first question anybody asks, and a store with no record cannot answer.
- **Re-running is safe.** The runner decides what is due from the last recorded
  run, so a cron that fires twice, or an operator who runs it by hand after an
  outage, costs a query and changes nothing.

The state lives in a JSON file beside the feature registry rather than in the
database, for the same reason the registry does: this has to work before Django
is fully up, and it must not need a migration to exist.
"""

from .runner import WorkerOutcome, due_workers, run_due  # noqa: F401
