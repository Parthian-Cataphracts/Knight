/**
 * A node store taking delivery of a Feature that is a service.
 *
 * The store runs none of its code, so most of what is asserted here is refusal:
 * an event this store does not publish, a slot it does not offer, a signed
 * document that disagrees with the job it arrived on
 * (docs/adr/0033-api-driven-features.md).
 *
 * The step list is KNIGHT's external install pipeline verbatim, and every verb
 * in it is one the in-process pipeline already had. That is the property that
 * makes the pivot safe for a store that has not been redeployed, and it is
 * asserted rather than assumed — this store spent four phases missing verbs
 * nobody had noticed it did not implement.
 */

import assert from 'node:assert/strict';
import { createHash, createSign, generateKeyPairSync } from 'node:crypto';
import { mkdtempSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { after, before, beforeEach, describe, it } from 'node:test';

import { JobRunner } from '../src/knight/runner.js';
import { Registry } from '../src/knight/registry.js';
import { getSettings } from '../src/knight/settings.js';
import { KNOWN_EVENTS, UI_SLOTS, subscribersFor } from '../src/knight/external.js';

/** KNIGHT's external install pipeline, verbatim. */
const PIPELINE = ['preflight', 'fetch', 'verify', 'backup', 'configure', 'install', 'enable', 'healthcheck'];

let temporary;
let settings;
let privateKey;
let publicKey;

before(() => {
  const pair = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  privateKey = pair.privateKey;
  publicKey = pair.publicKey;
});

beforeEach(() => {
  temporary = mkdtempSync(path.join(os.tmpdir(), 'knight-external-'));

  settings = getSettings({
    featureRoot: path.join(temporary, 'features'),
    workspace: path.join(temporary, 'workspace'),
    trustedKeys: { dev: publicKey.export({ type: 'spki', format: 'der' }).toString('base64') },
  });
});

after(() => {
  rmSync(temporary, { recursive: true, force: true });
});

function document(overrides = {}) {
  return {
    apiVersion: 'knight.dev/v1',
    architecture: 'external_service',
    slug: 'subscriptions',
    version: '2.0.0',
    name: 'Subscriptions',
    service: {
      base_url: 'https://subscriptions.knight.dev',
      auth: 'hmac-sha256',
      health: '/healthz',
      secret: 'SUBSCRIPTIONS_SERVICE_SECRET',
    },
    webhooks: [{ event: 'order.placed', path: '/hooks/order-placed', delivery: 'at-least-once' }],
    api_proxies: [{ prefix: 'subscriptions/', upstream: '/api/v1/', methods: ['GET', 'POST'], identity: 'customer' }],
    ui_mounts: [{ slot: 'admin.sidebar', label: 'Subscriptions', path: '/admin', kind: 'iframe' }],
    ...overrides,
  };
}

function job(overrides = {}) {
  const config = overrides.document ?? document();
  const body = Buffer.from(JSON.stringify(config));
  const artifactPath = path.join(temporary, `${config.slug}-${config.version}.json`);
  writeFileSync(artifactPath, body);

  const digest = createHash('sha256').update(body).digest('hex');
  const signature = createSign('SHA256').update(digest, 'ascii').sign(privateKey).toString('base64');

  const { document: _ignored, ...rest } = overrides;

  return {
    jobId: '00000000-0000-0000-0000-000000000001',
    type: 'Install',
    featureSlug: config.slug,
    targetVersion: config.version,
    // The field that tells the agent what the bytes it is about to fetch *are*.
    architecture: 'external_service',
    steps: PIPELINE,
    artifact: {
      packageReference: path.basename(artifactPath),
      digest,
      sizeBytes: body.length,
      signature,
      signingKeyId: 'dev',
      downloadUrl: pathToFileURL(artifactPath).href,
    },
    configuration: { version: 3, valuesJson: JSON.stringify({ plan: 'monthly' }), secrets: {} },
    runtime: { runtime: 'external', namespace: 'knight_subscriptions', module: 'subscriptions' },
    ...rest,
  };
}

describe('a node store taking delivery of a service', () => {
  it('registers webhooks, routes and screens without unpacking anything', async () => {
    const outcome = await new JobRunner(settings).run(job());

    assert.ok(outcome.succeeded, JSON.stringify(outcome));

    // No migrate step was named and none ran. This Feature has no schema in
    // this store, so there is nothing to migrate and nothing to reverse.
    assert.ok(!outcome.steps.some((step) => step.step === 'migrate'));

    const entry = await new Registry(settings.featureRoot).get('subscriptions');

    assert.equal(entry.contract.architecture, 'external_service');
    assert.equal(entry.contract.webhooks.length, 1);
    assert.equal(entry.contract.api_proxies.length, 1);
    assert.equal(entry.contract.ui_mounts.length, 1);
    assert.ok(entry.enabled);
  });

  it('creates no package directory, because there is no package', async () => {
    await new JobRunner(settings).run(job());

    // A directory here would be somewhere made for code that does not exist,
    // which the next person to look would read as a half-finished install.
    assert.ok(!existsSync(path.join(settings.featureRoot, 'subscriptions')));
  });

  it('names no verb the in-process pipeline does not already have', async () => {
    const { STEPS } = await import('../src/knight/steps.js');

    // The property that makes this pivot safe for a store nobody has
    // redeployed. Asserted rather than assumed: this store spent four phases
    // missing verbs nobody noticed it did not implement.
    for (const step of PIPELINE) {
      assert.ok(step in STEPS, `this store does not implement '${step}'`);
    }
  });

  it('refuses an event this store does not publish', async () => {
    const outcome = await new JobRunner(settings).run(
      job({ document: document({ webhooks: [{ event: 'order.plaecd', path: '/hooks/typo' }] }) }),
    );

    // Without this the Feature installs cleanly, passes its health check and
    // never hears anything. KNIGHT cannot make this check: it does not know
    // what any particular store publishes.
    assert.equal(outcome.code, 'install.unknown_event');
  });

  it('refuses a slot this store does not offer', async () => {
    const outcome = await new JobRunner(settings).run(
      job({ document: document({ ui_mounts: [{ slot: 'admin.nowhere', label: 'X', path: '/x' }] }) }),
    );

    assert.equal(outcome.code, 'install.unknown_slot');
  });

  it('refuses a signed document that disagrees with the job it arrived on', async () => {
    const outcome = await new JobRunner(settings).run(
      job({ document: document({ architecture: 'in_process' }) }),
    );

    // Acting on either would be choosing which of two disagreeing sources to
    // trust, and the honest answer is neither.
    assert.equal(outcome.code, 'install.wrong_architecture');
  });

  it('refuses a configuration whose digest does not match', async () => {
    const base = job();
    const outcome = await new JobRunner(settings).run({
      ...base,
      artifact: { ...base.artifact, digest: '0'.repeat(64) },
    });

    // The reason the configuration is signed at all: without this the store
    // would wire a proxy route, carrying its customers' requests, to whatever
    // host answered the download URL.
    assert.equal(outcome.failedStep, 'verify');
    assert.equal(outcome.code, 'digest.mismatch');
  });

  it('disables without unregistering, and uninstalls by unregistering', async () => {
    const runner = new JobRunner(settings);
    const registry = new Registry(settings.featureRoot);

    await runner.run(job());
    await runner.run(job({ type: 'Disable', steps: ['disable'] }));

    let entry = await registry.get('subscriptions');
    assert.ok(entry, 'disable must not unregister');
    assert.equal(entry.enabled, false);

    await runner.run(job({ type: 'Uninstall', steps: ['disable', 'backup', 'remove-package'] }));

    entry = await registry.get('subscriptions');
    assert.ok(entry === undefined || entry === null, 'uninstall must leave no registration');
  });

  it('rolls back to the registration the backup kept', async () => {
    const runner = new JobRunner(settings);
    const registry = new Registry(settings.featureRoot);

    await runner.run(job());

    const newer = document({ version: '2.1.0', webhooks: [{ event: 'order.paid', path: '/hooks/paid' }] });
    await runner.run(job({ document: newer }));

    assert.equal((await registry.get('subscriptions')).version, '2.1.0');

    const outcome = await runner.run(
      job({ type: 'Rollback', steps: ['restore-package', 'configure', 'enable', 'healthcheck'] }),
    );

    assert.ok(outcome.succeeded, JSON.stringify(outcome));

    const restored = await registry.get('subscriptions');

    // Restored from the local copy `backup` kept, not fetched: a rollback job
    // names the version it is rolling *to* and carries the artifact of the one
    // it is rolling *from*.
    assert.equal(restored.version, '2.0.0');
    assert.ok(restored.enabled);
    assert.equal(restored.contract.webhooks[0].event, 'order.placed');
  });

  it('fails a rollback with nothing kept rather than reporting success', async () => {
    const outcome = await new JobRunner(settings).run(
      job({ type: 'Rollback', steps: ['restore-package'] }),
    );

    assert.equal(outcome.code, 'rollback.no_backup');
  });

  it('tells only the Features that subscribed, and only while they are enabled', async () => {
    const runner = new JobRunner(settings);
    const registry = new Registry(settings.featureRoot);

    await runner.run(job());

    let subscribers = await subscribersFor(registry, 'order.placed');
    assert.equal(subscribers.length, 1);
    assert.equal(subscribers[0].slug, 'subscriptions');

    assert.equal((await subscribersFor(registry, 'order.refunded')).length, 0);

    await runner.run(job({ type: 'Disable', steps: ['disable'] }));

    // An entitlement that lapsed is a commercial fact and the store enforces it
    // now, not at the next restart.
    subscribers = await subscribersFor(registry, 'order.placed');
    assert.equal(subscribers.length, 0);
  });

  it('publishes a catalogue of events and slots that Features can be written against', () => {
    assert.ok(KNOWN_EVENTS.has('order.placed'));
    assert.ok(UI_SLOTS.has('admin.sidebar'));
    assert.ok([...KNOWN_EVENTS].every((name) => name.includes('.')));
  });
});
