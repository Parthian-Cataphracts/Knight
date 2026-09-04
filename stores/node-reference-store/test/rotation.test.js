/**
 * The store side of rotate-on-handshake (docs/hardening-backlog.md P2).
 *
 * When KNIGHT hands back a replacement credential on a handshake, this store
 * adopts it and authenticates with it from the next handshake on — the half
 * without which a rotation would lock the store out when the old secret's grace
 * ended.
 */

import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { after, beforeEach, describe, it } from 'node:test';

import { KnightClient } from '../src/knight/client.js';
import { activeCredential, adoptIfRotated, readStored } from '../src/knight/credentials.js';
import { getSettings } from '../src/knight/settings.js';

describe('credential adoption', () => {
  let root;

  beforeEach(() => {
    root = mkdtempSync(path.join(os.tmpdir(), 'knight-rotation-'));
  });

  after(() => {
    // beforeEach makes a fresh dir each test; clean the last one up.
    if (root) rmSync(root, { recursive: true, force: true });
  });

  const settings = (over = {}) =>
    getSettings({
      featureRoot: root,
      clientId: 'knight-shop-oldoldoldold',
      clientSecret: 'the-old-secret',
      environment: 'Development',
      ...over,
    });

  it('the environment is the credential until one is rotated', async () => {
    const s = settings();

    assert.equal(await readStored(s), null);
    assert.deepEqual(await activeCredential(s), {
      clientId: 'knight-shop-oldoldoldold',
      clientSecret: 'the-old-secret',
    });
  });

  it('a rotated credential is adopted and wins over the environment', async () => {
    const s = settings();

    const adopted = await adoptIfRotated(s, {
      accessToken: 'a-token',
      rotatedCredential: {
        clientId: 'knight-shop-newnewnewnew',
        clientSecret: 'the-new-secret',
        expiresAt: '2027-01-01T00:00:00Z',
      },
    });

    assert.equal(adopted, true);
    assert.deepEqual(await activeCredential(s), {
      clientId: 'knight-shop-newnewnewnew',
      clientSecret: 'the-new-secret',
    });
  });

  it('a handshake without a rotation adopts nothing', async () => {
    const s = settings();

    assert.equal(await adoptIfRotated(s, { accessToken: 'a-token' }), false);
    assert.equal(await adoptIfRotated(s, { accessToken: 'a-token', rotatedCredential: null }), false);
    assert.equal(await readStored(s), null);
  });

  it('an incomplete rotation is ignored rather than half adopted', async () => {
    const s = settings();

    assert.equal(await adoptIfRotated(s, { rotatedCredential: { clientId: 'knight-shop-newnewnewnew' } }), false);
    assert.deepEqual(await activeCredential(s), {
      clientId: 'knight-shop-oldoldoldold',
      clientSecret: 'the-old-secret',
    });
  });

  it('the handshake path authenticates with the old secret and adopts the new one', async () => {
    const s = settings();
    const requests = [];

    // A fake KNIGHT: it records the credential it was presented and hands back a
    // rotation.
    const fetchImpl = async (url, options) => {
      requests.push(JSON.parse(options.body));

      return new Response(
        JSON.stringify({
          accessToken: 'a-token',
          expiresIn: 1800,
          storeId: '00000000-0000-0000-0000-000000000001',
          rotatedCredential: { clientId: 'knight-shop-newnewnewnew', clientSecret: 'the-new-secret' },
        }),
        { status: 200, headers: { 'content-type': 'application/json' } },
      );
    };

    const original = globalThis.fetch;
    globalThis.fetch = fetchImpl;

    try {
      await new KnightClient(s).handshake();
    } finally {
      globalThis.fetch = original;
    }

    // The handshake authenticated as the old credential...
    assert.equal(requests[0].clientId, 'knight-shop-oldoldoldold');
    assert.equal(requests[0].clientSecret, 'the-old-secret');

    // ...and the replacement is now what is in force.
    assert.deepEqual(await activeCredential(s), {
      clientId: 'knight-shop-newnewnewnew',
      clientSecret: 'the-new-secret',
    });
  });
});
