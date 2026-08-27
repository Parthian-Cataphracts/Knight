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

    ...overrides,
  };
}
