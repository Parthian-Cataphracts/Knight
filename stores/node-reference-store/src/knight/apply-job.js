#!/usr/bin/env node
/**
 * `npm run apply-job -- <job.json>` — run one installation job KNIGHT queued.
 *
 * The same seam as the Django store's `manage.py knight_apply_job`, and a
 * command rather than a daemon for the same reason: a command exits with a
 * status, writes to a log, and can be run by hand during an incident to see
 * exactly what happens.
 *
 * It takes the job payload from a file rather than claiming it over HTTP, and
 * that boundary is deliberate and documented. What this store exists to
 * demonstrate is that the *delivery contract* is runtime-neutral; the transport
 * around it — token exchange, claiming, reporting — is identical to the Django
 * store's and duplicating it here would prove nothing new
 * (docs/adr/0032-a-feature-declares-its-runtime.md).
 */

import { readFile } from 'node:fs/promises';

import { JobRunner } from './runner.js';
import { getSettings } from './settings.js';

const [, , jobPath] = process.argv;

if (!jobPath) {
  console.error('Usage: npm run apply-job -- <path to the job payload>');
  process.exit(2);
}

const job = JSON.parse(await readFile(jobPath, 'utf8'));
const outcome = await new JobRunner(getSettings()).run(job);

for (const step of outcome.steps) {
  console.log(`  ${step.step.padEnd(12)} ${step.detail}`);
}

if (!outcome.succeeded) {
  console.error(`FAILED at ${outcome.failedStep}: ${outcome.code} — ${outcome.detail}`);
  process.exit(1);
}

console.log(`${outcome.slug} ${outcome.version ?? ''} installed.`);
