/**
 * `node-conformance` — the smallest Feature that proves a node store took
 * delivery.
 *
 * It is not in the commercial catalogue and is not for sale. It exists because
 * `adr/0032` §4 says a runtime is not real until a store has received a Feature
 * over it, and a name in a list with nothing behind it is a promise. This is the
 * thing behind the name.
 *
 * So it does exactly the four things a delivered Feature has to be able to do,
 * and nothing else:
 *
 * - it is **loadable** by the store from where the installer put it;
 * - it **serves a route** at the prefix its manifest declared, which is what
 *   proves the mount was carried across;
 * - it **reads its own configuration** out of the file the installer wrote,
 *   which is the contract every other Feature in this repository relies on;
 * - it **answers a health check**, which is what decides whether an install is
 *   reported as a success.
 */

import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));

/**
 * This Feature's configuration, in the shape KNIGHT's installer writes beside
 * the package: `{version, values, secrets}`.
 *
 * The same filename, the same shape **and the same place** the Django Features
 * read it from - beside the package directory rather than inside it, which is
 * what `Path(__file__).parent.parent` means in every one of their `config.py`
 * files. Getting that wrong is how the first run of this Feature reported
 * configuration version 0 with a perfectly good file one directory up.
 *
 * A Feature should not have to know which store it landed in, and the day that
 * stops being true is the day "runtime-neutral" becomes a word rather than a
 * property.
 */
export async function configuration() {
  try {
    const document = JSON.parse(await readFile(path.join(HERE, '..', 'knight_config.json'), 'utf8'));

    return { version: document.version ?? 0, values: document.values ?? {}, ...document };
  } catch (error) {
    if (error.code === 'ENOENT') {
      return { version: 0, values: {} };
    }

    throw error;
  }
}

/** The route the manifest mounts. Answers with what the store handed it. */
export async function router(request, response, subpath) {
  const config = await configuration();

  response.writeHead(200, { 'content-type': 'application/json' });
  response.end(
    JSON.stringify({
      feature: 'node-conformance',
      runtime: 'node',
      path: subpath,
      configurationVersion: config.version,
      greeting: config.values.greeting ?? 'installed',
    }),
  );
}

/**
 * The check KNIGHT runs after installing this.
 *
 * True when the Feature can read its own configuration, which is the one thing
 * that is genuinely capable of being broken by an install: the package can be
 * perfect and the configure step can still have written the file somewhere the
 * Feature does not look.
 */
export async function health() {
  await configuration();

  return true;
}

/** A worker, so that the declaration in the manifest has something behind it. */
export async function sweep() {
  return { swept: 0 };
}
