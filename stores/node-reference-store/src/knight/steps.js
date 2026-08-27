/**
 * The job vocabulary, carried out the way a node application does it.
 *
 * The step names are KNIGHT's and they are the same ones the Django store
 * performs — `preflight`, `fetch`, `verify`, `install`, `migrate`, `configure`,
 * `enable`, `healthcheck`. That is the whole claim of `adr/0032`: the vocabulary
 * was never Django's, only the manifest was, and once the wiring is named
 * neutrally a second runtime is a second implementation of the same eight verbs.
 *
 * What differs is only what each verb does. `install` unpacks a Python package
 * into a directory over there and an npm package into one here; `migrate` runs
 * `manage.py migrate` over there and this store's own ledger here. Neither store
 * decides *whether* to migrate — the job says.
 */

import { cp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';

import { readArchive } from './unzip.js';
import { verifyDigest, verifySignature } from './verify.js';

/** What this store is. Checked against what the job says it is delivering. */
export const RUNTIME = 'node';

export class StepFailed extends Error {
  constructor(code, detail) {
    super(detail);
    this.name = 'StepFailed';
    this.code = code;
    this.detail = detail;
  }
}

/**
 * The runtime wiring KNIGHT sends with the job.
 *
 * `runtime` since adr/0032; `django` is what KNIGHT still sends beside it for
 * stores that have not been upgraded, and this store has no use for it — but
 * reading it as a fallback costs one `||` and means a job built by an older
 * KNIGHT is refused for the right reason below rather than for a missing field.
 */
export function wiring(job) {
  return job.runtime || job.django || {};
}

/**
 * Refuses a package built for something this store does not run.
 *
 * First, before the download. A node store handed a Django package cannot
 * install it, and finding that out after unpacking means a half-installed
 * Feature and a store that has to be tidied up by hand. A job that names no
 * runtime is from a KNIGHT older than adr/0032 and is Django by definition —
 * which is exactly what this store must refuse.
 */
export function preflight(context) {
  const declared = wiring(context.job).runtime || 'django';

  if (declared !== RUNTIME) {
    throw new StepFailed(
      'preflight.wrong_runtime',
      `This store runs ${RUNTIME} and the job delivers a ${declared} package. Nothing was installed.`,
    );
  }

  const artifact = context.job.artifact;
  const installs = !['Disable', 'Enable', 'Uninstall', 'ApplyConfiguration'].includes(context.job.type);

  if (installs && !artifact) {
    throw new StepFailed('preflight.no_artifact', 'The job names no artifact to install.');
  }

  if (artifact && Number(artifact.sizeBytes || 0) > context.settings.maxArtifactBytes) {
    throw new StepFailed(
      'preflight.too_large',
      `The artifact is ${artifact.sizeBytes} bytes, above this store's limit of ${context.settings.maxArtifactBytes}.`,
    );
  }

  return `preflight ok for ${context.slug}`;
}

/**
 * Fetches the artifact.
 *
 * A `file:` URL is honoured so the whole path can be exercised without a network
 * or a bucket, which is what the tests and the conformance run use. Everything
 * else goes over HTTPS to the short-lived signed URL the job carries; the URL is
 * never stored, because a stored one outlives its own expiry.
 */
export async function fetchArtifact(context) {
  const artifact = context.job.artifact;
  const url = artifact?.downloadUrl;

  if (!url) {
    throw new StepFailed('fetch.no_url', 'The job carries no download URL.');
  }

  if (url.startsWith('file:')) {
    context.bytes = await readFile(new URL(url));
  } else {
    const response = await fetch(url);

    if (!response.ok) {
      throw new StepFailed('fetch.failed', `The artifact could not be downloaded: HTTP ${response.status}.`);
    }

    context.bytes = Buffer.from(await response.arrayBuffer());
  }

  return `fetched ${context.bytes.length} bytes`;
}

export function verify(context) {
  const artifact = context.job.artifact;

  const digest = verifyDigest(context.bytes, artifact?.digest);
  verifySignature(digest, artifact?.signature, artifact?.signingKeyId, context.settings.trustedKeys);

  return `verified ${digest}`;
}

/**
 * Unpacks the package into this store's feature root.
 *
 * Into a directory named for the Feature's slug rather than for its module,
 * because an npm specifier is scoped — `@knight/feature-x` is not a directory
 * name — and because a store operator looking at a directory listing wants to
 * see the thing they were sold.
 *
 * The previous version is kept until the install is known to work, which is what
 * makes `rollback` a rename rather than a re-download.
 */
export async function install(context) {
  const target = path.join(context.settings.featureRoot, context.slug);
  const previous = `${target}.previous`;

  if (existsSync(previous)) {
    await rm(previous, { recursive: true, force: true });
  }

  if (existsSync(target)) {
    const { rename } = await import('node:fs/promises');
    await rename(target, previous);
  }

  const entries = readArchive(context.bytes);

  for (const entry of entries) {
    const destination = path.join(target, entry.name);

    await mkdir(path.dirname(destination), { recursive: true });
    await writeFile(destination, entry.bytes);
  }

  context.installedPath = target;

  return `installed ${entries.length} file(s) into ${target}`;
}

/**
 * Records the Feature's schema as being at the version it was delivered at.
 *
 * A node store has no `manage.py migrate`, and inventing a migration runner in a
 * reference store would be inventing a framework. What it does have is the
 * obligation KNIGHT's contract actually places on it: know what state the
 * Feature's schema is in, under the **namespace** the manifest declared, so that
 * an upgrade and a rollback have something to move between.
 *
 * A real node store runs its own migrator here — knex, umzug, plain SQL — keyed
 * on the same namespace. The ledger below is what any of them would have to
 * write down afterwards.
 */
export async function migrate(context) {
  if (!context.job.migrations?.required) {
    return 'the job declares no migrations';
  }

  const namespace = wiring(context.job).namespace;

  if (!namespace) {
    throw new StepFailed('migrate.no_namespace', 'The job requires migrations and names no namespace to record them under.');
  }

  await context.registry.recordMigration(namespace, context.job.targetVersion || 'unknown');

  return `recorded ${namespace} at ${context.job.targetVersion || 'unknown'}`;
}

/**
 * Writes the configuration KNIGHT sent, beside the package.
 *
 * The same file the Django store writes and in the same shape, because it is the
 * Feature that reads it and a Feature should not have to know which store it
 * landed in. Secrets are written here too and never logged: they arrive
 * decrypted, and the only correct thing to do with them is put them where the
 * Feature expects and forget them.
 */
export async function configure(context) {
  const configuration = context.job.configuration;

  if (!configuration) {
    return 'no configuration to apply';
  }

  const document = {
    version: configuration.version ?? 0,
    values: configuration.valuesJson ? JSON.parse(configuration.valuesJson) : {},
    secrets: configuration.secrets ?? {},
  };

  await mkdir(context.installedPath ?? path.join(context.settings.featureRoot, context.slug), { recursive: true });
  await writeFile(
    path.join(context.installedPath ?? path.join(context.settings.featureRoot, context.slug), 'knight_config.json'),
    `${JSON.stringify(document, null, 2)}\n`,
    'utf8',
  );

  return `configuration v${document.version} written`;
}

/** Records the Feature as installed and switched on. */
export async function enable(context) {
  const declared = wiring(context.job);

  await context.registry.put({
    slug: context.slug,
    version: context.job.targetVersion ?? null,
    runtime: RUNTIME,
    namespace: declared.namespace ?? null,
    module: declared.module ?? null,
    mountExport: declared.mountExport ?? null,
    mountPrefix: declared.mountPrefix ?? null,
    workers: declared.workers ?? [],
    digest: context.job.artifact?.digest ?? null,
    enabled: true,
    installedAt: new Date().toISOString(),
  });

  return `${context.slug} enabled`;
}

export async function disable(context) {
  await context.registry.setEnabled(context.slug, false);

  return `${context.slug} disabled`;
}

/**
 * Removes the package and the registry entry.
 *
 * The data the Feature wrote is not touched here. What happens to that is the
 * uninstall policy's business and it is measured in years, not in the seconds
 * this step takes (adr/0016).
 */
export async function uninstall(context) {
  await rm(path.join(context.settings.featureRoot, context.slug), { recursive: true, force: true });
  await context.registry.remove(context.slug);

  return `${context.slug} removed`;
}

/**
 * Asks the Feature whether it works.
 *
 * The last step, and the one that decides whether the install is reported as a
 * success. An install that finishes and leaves a Feature that cannot answer is a
 * failed install — the Django store makes the same call, and both of them learnt
 * it the same way.
 */
export async function healthcheck(context) {
  const entry = await context.registry.get(context.slug);

  if (!entry) {
    throw new StepFailed('healthcheck.not_installed', `${context.slug} is not in this store's registry.`);
  }

  const check = context.job.healthCheck || entry.healthCheck;

  if (!check) {
    return 'the Feature declares no health check';
  }

  const [specifier, exported] = check.split('#');
  const loaded = await context.load(specifier, context.slug);
  const callable = loaded?.[exported];

  if (typeof callable !== 'function') {
    throw new StepFailed('healthcheck.missing', `${specifier} exports no function called '${exported}'.`);
  }

  if ((await callable()) !== true) {
    throw new StepFailed('healthcheck.failed', `${context.slug} reported itself unhealthy.`);
  }

  return `${context.slug} is healthy`;
}

/** The step name KNIGHT uses, to the function that carries it out. */
/**
 * Keeps the currently installed tree aside, so a rollback has something to
 * restore.
 *
 * A separate step from `install` even though `install` also moves the old tree
 * out of the way: the job says `backup` before `fetch` has even produced bytes
 * on some pipelines, and a store that only backed up as a side effect of
 * installing would have nothing kept when the install never ran.
 */
export async function backup(context) {
  const target = path.join(context.settings.featureRoot, context.slug);

  if (!existsSync(target)) {
    return 'nothing installed to back up';
  }

  const previous = `${target}.previous`;
  await rm(previous, { recursive: true, force: true });
  await cp(target, previous, { recursive: true });

  return `kept the current install at ${previous}`;
}

/**
 * Database extensions, which this store has no database to create.
 *
 * A no-op when the Feature declares none, and a refusal when it declares any:
 * succeeding at "create the extensions" without a database would tell KNIGHT
 * this store is ready for a Feature that will fail the moment it runs.
 *
 * The step exists here at all because the vocabulary is KNIGHT's, not the
 * runtime's (adr/0032). A store that does not know a verb refuses the job, and
 * this store did not know three of them until phase 20 — nobody noticed,
 * because its own fixture only ever named the eight it did know.
 */
export async function createExtensions(context) {
  const extensions = context.job.migrations?.extensions ?? [];

  if (extensions.length === 0) {
    return 'no extensions declared';
  }

  throw new StepFailed(
    'extensions.unsupported',
    `${context.slug} requires the database extension(s) ${extensions.join(', ')} and this store has no database.`,
  );
}

/**
 * Restarting, which this store cannot do to itself.
 *
 * Reported rather than performed, and reported truthfully: a module already
 * imported into this process stays imported, so the Feature is on disk and
 * registered but is not being served until somebody restarts the store. Saying
 * "reloaded" here would be the store telling KNIGHT a lie that only shows up as
 * a 404 a merchant reports.
 */
export async function reload(context) {
  return `${context.slug} is installed; this store serves it after a restart`;
}

export const STEPS = {
  preflight,
  fetch: fetchArtifact,
  verify,
  backup,
  install,
  'create-extensions': createExtensions,
  migrate,
  reload,
  configure,
  enable,
  disable,
  uninstall,
  healthcheck,
};
