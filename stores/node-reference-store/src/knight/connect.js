#!/usr/bin/env node
/**
 * `npm run connect` — handshake with KNIGHT and send one heartbeat.
 *
 * The Django store's `knight_register` and `knight_heartbeat`, in one command
 * because this store has no scheduler to separate them onto. What matters is
 * that both happen: a store that has handshaken but never heartbeated has told
 * KNIGHT it exists and not what it runs, and a store whose runtime KNIGHT does
 * not know is a store nothing can be delivered to.
 */

import { KnightClient } from './client.js';
import { getSettings } from './settings.js';

const settings = getSettings();

if (!settings.clientId || !settings.clientSecret) {
  console.error('Set KNIGHT_CLIENT_ID and KNIGHT_CLIENT_SECRET to the credential this store was issued.');
  process.exit(2);
}

const client = new KnightClient(settings);
const store = await client.handshake();

console.log(`Connected as ${store.storeName} (${store.slug}), integration ${store.integrationStatus}.`);

await client.heartbeat({ features: [] });

const runtime = client.runtime();

console.log(`Reported runtime ${runtime.name} ${runtime.node}.`);
