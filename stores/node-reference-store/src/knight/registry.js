/**
 * What this store has installed, and what state each Feature's schema is in.
 *
 * A JSON file, like the Django store's, and for the same reason: the registry
 * has to be readable by a person during an incident without a database being up.
 * It is versioned so that a future shape can be recognised rather than
 * misread — a registry read as empty is a store that reinstalls everything.
 */

import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

const SCHEMA_VERSION = 1;
const FILENAME = 'installed.json';

export class Registry {
  constructor(root) {
    this.path = path.join(root, FILENAME);
  }

  async #read() {
    try {
      const document = JSON.parse(await readFile(this.path, 'utf8'));

      if (document.schemaVersion !== SCHEMA_VERSION) {
        throw new Error(
          `The feature registry at ${this.path} is version ${document.schemaVersion}, and this store understands ${SCHEMA_VERSION}.`,
        );
      }

      return document;
    } catch (error) {
      if (error.code === 'ENOENT') {
        return { schemaVersion: SCHEMA_VERSION, features: {}, migrations: {} };
      }

      // Anything else is refused rather than treated as empty. A corrupt
      // registry read as "nothing is installed" is a store that reinstalls the
      // fleet and reruns every migration.
      throw error;
    }
  }

  async #write(document) {
    await mkdir(path.dirname(this.path), { recursive: true });
    await writeFile(this.path, `${JSON.stringify(document, null, 2)}\n`, 'utf8');
  }

  async all() {
    return (await this.#read()).features;
  }

  async get(slug) {
    return (await this.#read()).features[slug] ?? null;
  }

  async put(entry) {
    const document = await this.#read();
    document.features[entry.slug] = { ...(document.features[entry.slug] ?? {}), ...entry };
    await this.#write(document);
  }

  async setEnabled(slug, enabled) {
    const document = await this.#read();

    if (document.features[slug]) {
      document.features[slug].enabled = enabled;
      await this.#write(document);
    }
  }

  async remove(slug) {
    const document = await this.#read();
    delete document.features[slug];
    await this.#write(document);
  }

  /**
   * Records a Feature's schema as being at a version, under the namespace the
   * manifest declared.
   *
   * The namespace is the key and not the slug, deliberately: it is what the
   * manifest said the schema is recorded under, it is what a real migrator would
   * be keyed on, and it is the one name that survives a Feature being renamed.
   */
  async recordMigration(namespace, version) {
    const document = await this.#read();
    document.migrations[namespace] = { version, at: new Date().toISOString() };
    await this.#write(document);
  }

  async migrationOf(namespace) {
    return (await this.#read()).migrations[namespace] ?? null;
  }
}
