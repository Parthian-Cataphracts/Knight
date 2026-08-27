/**
 * A node store taking delivery of a Feature.
 *
 * This suite is the evidence behind `adr/0032` §4: a runtime is not real until a
 * store has received a Feature over it. Everything here runs against the actual
 * artifact `knight_package.py` builds from
 * `features/knight-feature-node-conformance`, verified against a real ECDSA
 * signature, unpacked by this store's own code, and then loaded and called.
 *
 * What is worth pinning is not "the happy path works". It is the four things
 * that decide whether a second runtime is genuinely equal to the first:
 *
 * - a package built for **another runtime is refused before anything is
 *   downloaded**, because the alternative is a half-installed store;
 * - the **signature is what is trusted**, not the digest and not the sender;
 * - the **mount travels**, so a Feature that declared a route serves it;
 * - and the **configuration lands where the Feature looks for it**, which is the
 *   one contract a Feature must not have to know which store it is in to rely
 *   on.
 */

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { mkdir, readFile } from 'node:fs/promises';
import { createHash, generateKeyPairSync, createSign } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { after, before, describe, it } from 'node:test';

import { JobRunner } from '../src/knight/runner.js';
import { getSettings } from '../src/knight/settings.js';
import { readArchive } from '../src/knight/unzip.js';

const REPO = path.resolve(import.meta.dirname, '..', '..', '..');
const FEATURE = path.join(REPO, 'features', 'knight-feature-node-conformance');
const KEY_ID = 'test-key';

let temporary;
let artifactPath;
let artifact;
let digest;
let signature;
let settings;

/**
 * Builds the real artifact with the real packaging tool.
 *
 * Not a fixture zip written by hand, deliberately. A hand-written one proves
 * this store can read a zip somebody wrote for it; this proves it can read what
 * KNIGHT actually publishes, which is the only claim worth making.
 */
before(() => {
  temporary = mkdtempSync(path.join(os.tmpdir(), 'knight-node-store-'));

  execFileSync(
    'python',
    [path.join(REPO, 'features', 'tools', 'knight_package.py'), 'build', FEATURE, '--dist', temporary],
    { stdio: 'pipe' },
  );

  artifactPath = path.join(temporary, 'node-conformance-1.0.0.zip');
  artifact = readFileSync(artifactPath);
  digest = `sha256:${createHash('sha256').update(artifact).digest('hex')}`;

  // A throwaway signing pair, used the way KNIGHT's is: ECDSA P-256 over the
  // ASCII digest string. The algorithm is the contract rather than this store's
  // choice — a store that verified some other way would accept artifacts KNIGHT
  // never signed.
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });

  signature = createSign('SHA256').update(digest, 'ascii').sign(privateKey).toString('base64');

  settings = getSettings({
    featureRoot: path.join(temporary, 'features'),
    workspace: path.join(temporary, 'workspace'),
    trustedKeys: { [KEY_ID]: publicKey.export({ type: 'spki', format: 'der' }).toString('base64') },
  });
});

after(() => {
  rmSync(temporary, { recursive: true, force: true });
});

/** The job payload KNIGHT sends, in the shape `StoreJobEndpoints` produces. */
function job(overrides = {}) {
  return {
    jobId: '00000000-0000-0000-0000-000000000001',
    type: 'Install',
    featureSlug: 'node-conformance',
    targetVersion: '1.0.0',
    steps: ['preflight', 'fetch', 'verify', 'install', 'migrate', 'configure', 'enable', 'healthcheck'],
    artifact: {
      packageReference: 'node-conformance-1.0.0.zip',
      digest,
      sizeBytes: artifact.length,
      signature,
      signingKeyId: KEY_ID,
      downloadUrl: pathToFileURL(artifactPath).href,
    },
    migrations: { required: true, reversible: true, requiresMaintenanceWindow: false, extensions: [] },
    configuration: { version: 3, valuesJson: JSON.stringify({ greeting: 'delivered' }), secrets: {} },
    runtime: {
      runtime: 'node',
      namespace: 'knight_node_conformance',
      module: '@knight/feature-node-conformance',
      mountExport: 'router',
      mountPrefix: 'conformance/',
      workers: [{ name: 'sweep', entrypoint: '@knight/feature-node-conformance#sweep', schedule: 'daily' }],
    },
    healthCheck: '@knight/feature-node-conformance#health',
    ...overrides,
  };
}

describe('a node store taking delivery', () => {
  it('installs a Feature end to end and reports every step', async () => {
    const outcome = await new JobRunner(settings).run(job());

    assert.equal(outcome.succeeded, true, JSON.stringify(outcome));
    assert.deepEqual(
      outcome.steps.map((step) => step.step),
      ['preflight', 'fetch', 'verify', 'install', 'migrate', 'configure', 'enable', 'healthcheck'],
    );
  });

  it('records the Feature under the namespace the manifest declared', async () => {
    const runner = new JobRunner(settings);
    await runner.run(job());

    // The namespace and not the slug. It is what the manifest said the schema is
    // recorded under, it is what a real migrator would be keyed on, and it is
    // the one name that survives the Feature being renamed.
    const recorded = await runner.registry.migrationOf('knight_node_conformance');

    assert.equal(recorded?.version, '1.0.0');
  });

  it('writes the configuration where the Feature looks for it', async () => {
    const runner = new JobRunner(settings);
    await runner.run(job());

    const written = JSON.parse(
      await readFile(path.join(settings.featureRoot, 'node-conformance', 'knight_config.json'), 'utf8'),
    );

    // The same filename and the same shape the Django Features read. A Feature
    // should not have to know which store it landed in.
    assert.equal(written.version, 3);
    assert.equal(written.values.greeting, 'delivered');
  });

  it('loads the Feature and gets a working route out of it', async () => {
    const runner = new JobRunner(settings);
    await runner.run(job());

    const module = await runner.load('@knight/feature-node-conformance', 'node-conformance');

    // The mount travelled: the store knows which exported symbol serves requests
    // because the manifest said so and KNIGHT carried it.
    assert.equal(typeof module.router, 'function');

    const written = [];
    const response = {
      writeHead() {},
      end(body) {
        written.push(body);
      },
    };

    await module.router({}, response, '/');

    const body = JSON.parse(written[0]);

    assert.equal(body.runtime, 'node');
    assert.equal(body.configurationVersion, 3);
    assert.equal(body.greeting, 'delivered');
  });
});

describe('what a node store refuses', () => {
  it('refuses a package built for another runtime, before it downloads anything', async () => {
    const outcome = await new JobRunner(settings).run(
      job({ runtime: { ...job().runtime, runtime: 'django' } }),
    );

    assert.equal(outcome.succeeded, false);
    assert.equal(outcome.failedStep, 'preflight');
    assert.equal(outcome.code, 'preflight.wrong_runtime');

    // Nothing was fetched. The point of refusing in preflight is that the store
    // is untouched afterwards.
    assert.deepEqual(outcome.steps, []);
  });

  it('treats a job from a KNIGHT that predates runtimes as a Django job', async () => {
    const { runtime, ...withoutRuntime } = job();
    const outcome = await new JobRunner(settings).run({
      ...withoutRuntime,
      django: { appLabel: 'knight_x', installedApp: 'knight_feature_x', workers: [] },
    });

    // Absent means django, which is what the field defaults to everywhere else.
    // A node store must refuse it rather than assume it was meant for them.
    assert.equal(outcome.code, 'preflight.wrong_runtime');
  });

  it('reports every step as it finishes, not only at the end', async () => {
    const reported = [];

    const outcome = await new JobRunner(settings).run(job(), {
      onStep: (event) => reported.push(event),
    });

    assert.ok(outcome.succeeded, JSON.stringify(outcome));

    // One report per step, in order, each with a status KNIGHT understands. A
    // job that reports only at the end looks hung for the whole of a long
    // migration, and looks identical to one that died.
    assert.deepEqual(
      reported.map((event) => event.step),
      ['preflight', 'fetch', 'verify', 'install', 'migrate', 'configure', 'enable', 'healthcheck'],
    );
    assert.ok(reported.every((event) => event.status === 'Succeeded'));
  });

  it('reports the step that failed, with its own code', async () => {
    const reported = [];

    await new JobRunner(settings).run(job({ artifact: { ...job().artifact, digest: 'sha256:0000' } }), {
      onStep: (event) => reported.push(event),
    });

    const last = reported.at(-1);

    // The failure is reported like any other outcome. A job that stops
    // reporting when it goes wrong is a job whose last known state is the step
    // before the problem.
    assert.equal(last.step, 'verify');
    assert.equal(last.status, 'Failed');
    assert.equal(last.code, 'digest.mismatch');
  });

  it('finishes the job even when reporting a step to KNIGHT throws', async () => {
    const outcome = await new JobRunner(settings).run(job(), {
      onStep: () => {
        throw new Error('the control plane went away');
      },
    });

    // A store that abandoned an install because a progress report did not go
    // through would be a store where a flaky network uninstalls Features. The
    // outcome is reported again at the end, so nothing is lost.
    assert.ok(outcome.succeeded, JSON.stringify(outcome));
  });

  it('refuses a download that does not hash to what the job says', async () => {
    const outcome = await new JobRunner(settings).run(
      job({ artifact: { ...job().artifact, digest: 'sha256:0000' } }),
    );

    assert.equal(outcome.failedStep, 'verify');
    assert.equal(outcome.code, 'digest.mismatch');
  });

  it('refuses a valid artifact signed by a key it does not trust', async () => {
    const stranger = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
    const forged = createSign('SHA256').update(digest, 'ascii').sign(stranger.privateKey).toString('base64');

    const outcome = await new JobRunner(settings).run(
      job({ artifact: { ...job().artifact, signature: forged } }),
    );

    // The digest is right, the bytes are right, and the signature is real — by
    // the wrong key. This is the check that distinguishes "arrived intact" from
    // "KNIGHT published it".
    assert.equal(outcome.failedStep, 'verify');
    assert.equal(outcome.code, 'signature.invalid');
  });

  it('refuses an unsigned artifact', async () => {
    const outcome = await new JobRunner(settings).run(
      job({ artifact: { ...job().artifact, signature: '' } }),
    );

    assert.equal(outcome.code, 'signature.missing');
  });

  it('refuses a step it has never heard of rather than skipping it', async () => {
    const outcome = await new JobRunner(settings).run(job({ steps: ['preflight', 'reticulate'] }));

    // Skipping would let a KNIGHT that had learnt a new verb believe this store
    // had performed it.
    assert.equal(outcome.code, 'step.unknown');
  });

  it('refuses to load a package that calls itself something else', async () => {
    const runner = new JobRunner(settings);
    await runner.run(job());

    await assert.rejects(
      () => runner.load('@knight/feature-something-else', 'node-conformance'),
      (error) => error.code === 'load.wrong_package',
    );
  });
});

describe('the archive reader', () => {
  it('reads what the packaging tool actually produced', () => {
    const names = readArchive(artifact).map((entry) => entry.name);

    assert.ok(names.includes('knight_manifest.yaml'), names.join(', '));
    assert.ok(names.includes('package.json'), names.join(', '));
    assert.ok(names.includes('knight_node_conformance/index.js'), names.join(', '));
  });

  it('refuses an archive whose entries point outside the package', () => {
    // A delivered artifact reaching out of the directory it was given. Built by
    // hand because no honest packaging tool would produce one — which is exactly
    // why the store cannot assume none ever will.
    const evil = buildArchiveWith('../../escaped.js');

    assert.throws(() => readArchive(evil), /points outside the package/);
  });

  it('refuses a compression method it cannot read rather than guessing', () => {
    const bad = buildArchiveWith('fine.js', 99);

    assert.throws(() => readArchive(bad), /Compression method 99/);
  });
});

/**
 * A minimal stored-entry zip, written by hand so the reader can be shown the
 * inputs it must refuse.
 */
function buildArchiveWith(name, method = 0) {
  const bytes = Buffer.from('console.log(1);\n', 'utf8');
  const nameBytes = Buffer.from(name, 'utf8');

  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0);
  local.writeUInt16LE(method, 8);
  local.writeUInt32LE(bytes.length, 18);
  local.writeUInt32LE(bytes.length, 22);
  local.writeUInt16LE(nameBytes.length, 26);

  const localRecord = Buffer.concat([local, nameBytes, bytes]);

  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0);
  central.writeUInt16LE(method, 10);
  central.writeUInt32LE(bytes.length, 20);
  central.writeUInt32LE(bytes.length, 24);
  central.writeUInt16LE(nameBytes.length, 28);
  central.writeUInt32LE(0, 42);

  const centralRecord = Buffer.concat([central, nameBytes]);

  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(1, 8);
  end.writeUInt16LE(1, 10);
  end.writeUInt32LE(centralRecord.length, 12);
  end.writeUInt32LE(localRecord.length, 16);

  return Buffer.concat([localRecord, centralRecord, end]);
}
