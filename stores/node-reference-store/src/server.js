/**
 * The store itself: a shop front with installed Features mounted into it.
 *
 * Deliberately tiny. This is not a storefront and does not pretend to be one —
 * it is the smallest thing that can honestly answer "did the Feature get
 * installed, and can a request reach it", which is the only question this store
 * exists to answer.
 *
 * Features are mounted from the registry at start-up, under the prefix the
 * manifest declared and KNIGHT delivered. Nothing is hard-coded: a Feature that
 * declared no mount serves nothing and is not an error, because plenty of
 * Features are jobs and tables and no routes at all.
 */

import http from 'node:http';

import { JobRunner } from './knight/runner.js';
import { getSettings } from './knight/settings.js';

const settings = getSettings();
const runner = new JobRunner(settings);

/** `prefix` -> the handler a Feature exported. */
const mounted = new Map();

async function mountInstalledFeatures() {
  const features = await runner.registry.all();

  for (const [slug, entry] of Object.entries(features)) {
    if (!entry.enabled || !entry.mountExport || !entry.mountPrefix) {
      continue;
    }

    try {
      const module = await runner.load(entry.module, slug);
      const handler = module?.[entry.mountExport];

      if (typeof handler !== 'function') {
        // Loud, and then carry on serving. A Feature whose router will not load
        // must not take the shop down with it - the same call the Django store's
        // loader makes.
        console.error(`${slug}: '${entry.mountExport}' is not a function; nothing mounted.`);
        continue;
      }

      mounted.set(`/${entry.mountPrefix.replace(/^\/|\/$/g, '')}`, handler);
      console.log(`mounted ${slug} at /${entry.mountPrefix}`);
    } catch (error) {
      console.error(`${slug}: could not be mounted: ${error.message}`);
    }
  }
}

await mountInstalledFeatures();

const server = http.createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');

  if (url.pathname === '/health') {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ status: 'ok', features: [...mounted.keys()] }));
    return;
  }

  for (const [prefix, handler] of mounted) {
    if (url.pathname === prefix || url.pathname.startsWith(`${prefix}/`)) {
      await handler(request, response, url.pathname.slice(prefix.length) || '/');
      return;
    }
  }

  response.writeHead(404, { 'content-type': 'application/json' });
  response.end(JSON.stringify({ error: 'No route here.' }));
});

const port = Number(process.env.PORT || 8100);
server.listen(port, () => console.log(`node reference store on http://localhost:${port}`));
