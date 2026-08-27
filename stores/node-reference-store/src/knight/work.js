#!/usr/bin/env node
/**
 * `npm run work` — claim jobs from KNIGHT and run them until there are none.
 *
 * The counterpart of the Django store's `manage.py knight_apply_job`, and a
 * command rather than a daemon for the same reason: a command exits with a
 * status, writes to a log, and can be run by hand during an incident to see
 * exactly what happens.
 *
 * One job at a time and no concurrency. KNIGHT hands a job out already claimed,
 * so two agents cannot hold the same one — but two jobs for the same Feature
 * running side by side in one store would be a race this store would lose, and
 * there is nothing to win by having it.
 */

import { JobRunner } from './runner.js';
import { KnightClient } from './client.js';
import { getSettings } from './settings.js';

const maximum = Number(process.argv[2] || process.env.KNIGHT_MAX_JOBS || 10);
const settings = getSettings();

if (!settings.clientId || !settings.clientSecret) {
  console.error('Set KNIGHT_CLIENT_ID and KNIGHT_CLIENT_SECRET to the credential this store was issued.');
  process.exit(2);
}

const client = new KnightClient(settings);
const runner = new JobRunner(settings);

let ran = 0;
let failed = 0;

while (ran < maximum) {
  const job = await client.claimJob();

  if (!job) {
    break;
  }

  ran += 1;
  console.log(`[${job.type}] ${job.featureSlug} ${job.targetVersion ?? ''}`);

  const outcome = await runner.run(job, {
    onStep: ({ step, status, detail, code, durationMs }) =>
      client.reportStep(job.jobId, step, status, { output: detail, errorCode: code, durationMs }),
  });

  for (const step of outcome.steps) {
    console.log(`  ${step.step.padEnd(12)} ${step.detail}`);
  }

  if (outcome.succeeded) {
    await client.completeJob(job.jobId, {
      succeeded: true,
      installedVersion: outcome.version,
      health: 'Healthy',
    });

    console.log(`  -> ${job.featureSlug} ${outcome.version ?? ''} installed.`);
    continue;
  }

  failed += 1;

  // Reported as a failure, with the step's own code. "Install failed" and "the
  // signature was wrong" need different people woken up, and a job that failed
  // silently is a Feature a merchant has paid for and does not have.
  await client.completeJob(job.jobId, {
    succeeded: false,
    failureCode: outcome.code,
    failureMessage: outcome.detail,
    health: 'Unhealthy',
  });

  console.error(`  -> FAILED at ${outcome.failedStep}: ${outcome.code} — ${outcome.detail}`);
}

console.log(`${ran} job(s) run, ${failed} failed.`);

// A failed job is not a failed command: the store did its part, reported it, and
// the outcome is KNIGHT's to act on. The exit status says whether the agent
// itself worked.
process.exit(0);
