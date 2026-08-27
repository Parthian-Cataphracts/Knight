/**
 * Checking that a downloaded artifact is the one KNIGHT published.
 *
 * Two checks, and they answer different questions. The **digest** answers "did
 * this arrive intact"; the **signature** answers "did KNIGHT publish it". A
 * store that checks only the first trusts whoever served the bytes, which for a
 * signed download URL is a bucket and a CDN.
 *
 * The digest is computed from the file rather than taken from the payload.
 * Comparing the payload's digest to itself is a check that always passes.
 *
 * ECDSA P-256 over the ASCII digest string, which is exactly what the Django
 * reference store verifies and what `knight_package.py` produces. The algorithm
 * is not this store's choice — it is the contract, and a store that verified
 * some other way would accept artifacts KNIGHT never signed.
 */

import { createHash, createVerify } from 'node:crypto';

export class ArtifactRejected extends Error {
  constructor(code, message) {
    super(message);
    this.name = 'ArtifactRejected';
    this.code = code;
  }
}

/**
 * Lowercase hex over the bytes as they arrived, with no algorithm prefix.
 *
 * The shape is the contract's, not this store's preference: `knight_package.py`
 * publishes `hexdigest()` and **signs that exact ASCII string**, and the Django
 * reference store compares the same. This store used to compute `sha256:<hex>`
 * and verify the signature over that, so it could never have accepted a real
 * KNIGHT artifact — every download would have failed the digest check against
 * an identical-looking value, and had it got past that, the signature was over
 * different bytes.
 *
 * Nothing noticed from phase 17 to phase 20 because this store only ever saw a
 * job payload its own tests had built, from the same wrong assumption. It found
 * out the first time it claimed a job from KNIGHT.
 */
export function digestOf(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

export function verifyDigest(bytes, expected) {
  const actual = digestOf(bytes);
  const wanted = (expected ?? '').trim().toLowerCase();

  if (!wanted) {
    throw new ArtifactRejected('digest.missing', 'The job names no digest to check the download against.');
  }

  if (actual !== wanted) {
    throw new ArtifactRejected(
      'digest.mismatch',
      `The download hashes to ${actual} and the job says ${wanted}.`,
    );
  }

  return actual;
}

/**
 * The signature over the digest, against a key this store already trusts.
 *
 * Trusted keys are configuration, never anything the payload carries. A store
 * that took the key from the same message as the signature has verified that
 * the message is internally consistent and nothing else.
 */
export function verifySignature(digest, signature, keyId, trustedKeys) {
  if (!signature) {
    throw new ArtifactRejected('signature.missing', 'The artifact is not signed.');
  }

  const publicKeyDer = (trustedKeys || {})[keyId];

  if (!publicKeyDer) {
    throw new ArtifactRejected(
      'signature.unknown_key',
      `The artifact is signed with key '${keyId}', which this store does not trust.`,
    );
  }

  let ok = false;

  try {
    ok = createVerify('SHA256')
      .update(digest, 'ascii')
      .verify(
        { key: Buffer.from(publicKeyDer, 'base64'), format: 'der', type: 'spki' },
        Buffer.from(signature, 'base64'),
      );
  } catch (error) {
    throw new ArtifactRejected('signature.bad_key', `Key '${keyId}' could not be read: ${error.message}`);
  }

  if (!ok) {
    throw new ArtifactRejected(
      'signature.invalid',
      `The signature over the artifact digest is not valid for key '${keyId}'.`,
    );
  }
}
