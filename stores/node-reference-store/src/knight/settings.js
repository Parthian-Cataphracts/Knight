/**
 * What this store needs to know about KNIGHT, from the environment.
 *
 * Environment rather than a file, and read once: a store that re-read its
 * configuration mid-job could install under one set of rules and enable under
 * another.
 */

import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

export function getSettings(overrides = {}) {
  return {
    /** Where installed packages and the registry live. */
    featureRoot: process.env.KNIGHT_FEATURE_ROOT || path.join(ROOT, 'knight-features'),

    /** Scratch space for downloads. */
    workspace: process.env.KNIGHT_WORKSPACE || path.join(ROOT, 'workspace'),

    /**
     * The signing keys this store trusts, as `{keyId: base64 SPKI DER}`.
     *
     * Configuration and never anything a payload carries. A store that took the
     * key from the same message as the signature has checked that the message
     * agrees with itself.
     */
    trustedKeys: JSON.parse(process.env.KNIGHT_TRUSTED_KEYS || '{}'),

    /** A ceiling, because a download with no limit is a disk with no floor. */
    maxArtifactBytes: Number(process.env.KNIGHT_MAX_ARTIFACT_BYTES || 64 * 1024 * 1024),

    /**
     * Where KNIGHT is, and the credential this store was issued.
     *
     * Environment rather than a file for the same reason as everything above,
     * and the secret is never written anywhere by this store: it arrives in the
     * environment, it is exchanged for a short-lived token, and the token is
     * held in memory for the life of one command.
     */
    baseUrl: (process.env.KNIGHT_BASE_URL || 'http://localhost:5008').replace(/\/$/, ''),
    clientId: process.env.KNIGHT_CLIENT_ID || '',
    clientSecret: process.env.KNIGHT_CLIENT_SECRET || '',
    environment: process.env.KNIGHT_ENVIRONMENT || 'Development',
    storeVersion: process.env.KNIGHT_STORE_VERSION || '1.0.0',

    /** Nothing here is on a request path, and a control plane that has gone away must never become one. */
    timeoutMs: Number(process.env.KNIGHT_TIMEOUT_MS || 30_000),

    ...overrides,
  };
}
