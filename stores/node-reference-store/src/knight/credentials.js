/**
 * The credential in force, and where a rotated one is kept.
 *
 * The store is issued a client id and secret and, until rotation, they arrived
 * one way only: the environment. That is still the floor. But KNIGHT can now
 * rotate a credential nearing expiry and hand the replacement back on the
 * handshake that used the old one (docs/hardening-backlog.md P2), and a
 * replacement the store throws away is a store that is locked out the moment the
 * old secret's grace ends.
 *
 * So a rotated credential is persisted — a small file beside the feature
 * registry — and it takes precedence over the environment from then on. A store
 * that has never rotated writes nothing here at all.
 */

import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';

const FILENAME = 'knight-credential.json';

function credentialPath(settings) {
  return path.join(settings.featureRoot, FILENAME);
}

/**
 * The persisted credential, or null when there is not a complete one.
 *
 * Unreadable is treated as absent rather than fatal: a store that refused to
 * start because a credential file was truncated would be down for a reason
 * unrelated to selling anything. It falls back to the environment.
 */
export async function readStored(settings) {
  let stored;

  try {
    stored = JSON.parse(await readFile(credentialPath(settings), 'utf8'));
  } catch {
    return null;
  }

  const clientId = String(stored.clientId ?? '');
  const clientSecret = String(stored.clientSecret ?? '');

  return clientId && clientSecret ? { clientId, clientSecret } : null;
}

/**
 * The credential every handshake authenticates with: the persisted one when
 * there is a complete one, the environment otherwise.
 */
export async function activeCredential(settings) {
  return (await readStored(settings)) ?? { clientId: settings.clientId, clientSecret: settings.clientSecret };
}

/** Persists a credential KNIGHT rotated, so every handshake from now on uses it. */
export async function saveRotated(settings, clientId, clientSecret) {
  const target = credentialPath(settings);
  await mkdir(path.dirname(target), { recursive: true });

  // 0o600 where the platform honours it — it holds a secret, and the one KNIGHT
  // will never hand back again.
  await writeFile(target, `${JSON.stringify({ clientId, clientSecret })}\n`, { encoding: 'utf8', mode: 0o600 });
}

/**
 * Adopts a rotated credential a handshake handed back, if it did. Returns whether
 * one was adopted. The credential just used keeps working through its grace
 * window, so the current token stays valid; the next handshake picks up the
 * replacement stored here.
 */
export async function adoptIfRotated(settings, handshakeBody) {
  const rotated = handshakeBody?.rotatedCredential;

  if (!rotated || typeof rotated !== 'object') {
    return false;
  }

  const clientId = String(rotated.clientId ?? '');
  const clientSecret = String(rotated.clientSecret ?? '');

  if (!clientId || !clientSecret) {
    return false;
  }

  await saveRotated(settings, clientId, clientSecret);

  return true;
}

/** Removes a persisted credential, so the store falls back to the environment. */
export async function forgetStored(settings) {
  await rm(credentialPath(settings), { force: true });
}
