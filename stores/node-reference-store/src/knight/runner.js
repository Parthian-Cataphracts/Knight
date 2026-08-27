/**
 * Running one job's steps in order, and reporting what happened.
 *
 * The order comes from KNIGHT — the job carries its own step list — rather than
 * being decided here. A store that chose its own order would be a store where
 * "install then migrate" meant something different depending on who wrote it,
 * and the whole point of the vocabulary is that it does not.
 *
 * Two properties this is built around, both learnt on the Django store:
 *
 * - **a step that fails stops the job**, and the failure carries the step's own
 *   code, because "install failed" and "the signature was wrong" need different
 *   people woken up;
 * - **the outcome is recorded whether it succeeded or not**. A job that failed
 *   silently is a Feature a merchant has paid for and does not have.
 */

import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { Registry } from './registry.js';
import { getSettings } from './settings.js';
import { STEPS, StepFailed, wiring } from './steps.js';

export class JobRunner {
  constructor(settings = getSettings()) {
    this.settings = settings;
    this.registry = new Registry(settings.featureRoot);
  }

  /**
   * Loads a module out of an installed Feature.
   *
   * By path rather than by npm resolution, because the package was delivered
   * into this store's feature root rather than installed from a registry — which
   * is the whole delivery model. A store that ran `npm install` here would be
   * fetching code KNIGHT never signed.
   */
  async load(specifier, slug) {
    const root = path.join(this.settings.featureRoot, slug);
    const manifest = await this.#packageJson(root);

    // The specifier in a manifest is the package's npm name. What this store has
    // is a directory, so the name is checked against package.json and the entry
    // point is read from it.
    if (manifest?.name && specifier !== manifest.name && !specifier.startsWith(`${manifest.name}/`)) {
      throw new StepFailed(
        'load.wrong_package',
        `The job asks for '${specifier}' and the installed package calls itself '${manifest.name}'.`,
      );
    }

    const entry = path.join(root, manifest?.main || 'index.js');

    return import(pathToFileURL(entry).href);
  }

  async #packageJson(root) {
    try {
      const { readFile } = await import('node:fs/promises');

      return JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
    } catch (error) {
      if (error.code === 'ENOENT') {
        return null;
      }

      throw error;
    }
  }

  /**
   * Runs every step the job names, in the order it names them.
   *
   * Returns what happened rather than throwing, because the caller's job is to
   * report it to KNIGHT — including, and especially, the failures.
   *
   * `onStep` is called as each step finishes, succeeded or failed, so a caller
   * driving this against a live KNIGHT can report progress while the job is
   * still running. A job that reports only at the end is a job that looks hung
   * for the whole of a long migration, and looks identical to one that died.
   */
  async run(job, { onStep = null } = {}) {
    await mkdir(this.settings.workspace, { recursive: true });

    const context = {
      job,
      slug: job.featureSlug,
      settings: this.settings,
      registry: this.registry,
      load: (specifier, slug) => this.load(specifier, slug),
      bytes: null,
      installedPath: null,
    };

    const steps = job.steps?.length ? job.steps : ['preflight', 'fetch', 'verify', 'install', 'migrate', 'configure', 'enable', 'healthcheck'];
    const done = [];

    for (const name of steps) {
      const step = STEPS[name];

      if (!step) {
        // An unknown step is refused rather than skipped. Skipping one would let
        // a KNIGHT that had learnt a new verb believe this store had performed
        // it.
        await report(onStep, name, 'Failed', `This store does not know how to '${name}'.`, 'step.unknown', 0);

        return {
          succeeded: false,
          failedStep: name,
          code: 'step.unknown',
          detail: `This store does not know how to '${name}'.`,
          steps: done,
        };
      }

      const started = Date.now();

      try {
        const detail = await step(context);

        done.push({ step: name, detail });
        await report(onStep, name, 'Succeeded', detail, null, Date.now() - started);
      } catch (error) {
        const code = error instanceof StepFailed ? error.code : (error.code ?? 'step.failed');

        await report(onStep, name, 'Failed', error.message, code, Date.now() - started);

        return {
          succeeded: false,
          failedStep: name,
          code,
          detail: error.message,
          steps: done,
        };
      }
    }

    return {
      succeeded: true,
      slug: context.slug,
      version: job.targetVersion ?? null,
      runtime: wiring(job).runtime ?? null,
      steps: done,
    };
  }
}

/**
 * Tells the caller about one step, and never lets that stop the job.
 *
 * A store that abandoned an install because the progress report did not go
 * through would be a store where a flaky network uninstalls Features. The
 * outcome is reported again at the end, so nothing is lost by a report that
 * failed on the way.
 */
async function report(onStep, step, status, detail, code, durationMs) {
  if (!onStep) {
    return;
  }

  try {
    await onStep({ step, status, detail, code, durationMs });
  } catch {
    // Deliberately swallowed. See above.
  }
}
