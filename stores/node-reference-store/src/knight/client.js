/**
 * The HTTP client to KNIGHT.
 *
 * The transport phase 17 deliberately left out of this store, and phase 20 put
 * back. The reasoning at the time was sound — the delivery *contract* is what
 * runtime neutrality is about, and the transport around it is identical for
 * every runtime — but the conclusion was not: a store that never asks KNIGHT for
 * work is a store nobody has ever proved KNIGHT can hand work to. Phase 20 found
 * that KNIGHT could not have planned against this store at all, because
 * compatibility was decided on Python and Django versions it has no way to
 * report, and nothing noticed for three phases.
 *
 * Deliberately the same three properties as the Django store's client, because
 * they are properties of the contract rather than of Python:
 *
 * - **outbound only.** The store asks; KNIGHT never connects inward. That is
 *   what lets a store sit behind a firewall with no inbound port.
 * - **a 401 is recoverable exactly once.** Tokens expire; when one does, the
 *   client handshakes again and retries. A second 401 means the credential is
 *   wrong and retrying harder will not fix it.
 * - **every call has a timeout.** Nothing here is on a shopper's request path,
 *   and a control plane that has gone away must never become one.
 */

export class KnightUnavailable extends Error {}

export class KnightRejected extends Error {
  constructor(status, detail, code = '') {
    super(`KNIGHT refused the request (${status}): ${detail}`);
    this.status = status;
    this.detail = detail;
    this.code = code;
  }
}

export class KnightClient {
  constructor(settings) {
    this.settings = settings;
    this.token = null;
    this.store = null;
  }

  /**
   * Exchanges the client credential for a store token.
   *
   * The nonce makes a captured body useless a second time: KNIGHT remembers it
   * for the length of its window and refuses a replay.
   */
  async handshake() {
    const body = await this.#request('POST', '/api/v1/ingest/handshake', {
      clientId: this.settings.clientId,
      clientSecret: this.settings.clientSecret,
      environment: this.settings.environment,
      storeVersion: this.settings.storeVersion,
      runtime: `Node ${process.versions.node}`,
      nonce: crypto.randomUUID(),
    }, { authenticated: false });

    this.token = body.accessToken;
    this.store = body;

    return body;
  }

  /**
   * What this store runs, for KNIGHT's compatibility checks.
   *
   * `name` before any version of anything: KNIGHT decides from it which of the
   * other names mean anything, and refuses a Feature built for another runtime
   * by name rather than by failing version comparisons it cannot make. A store
   * that omits it cannot be planned against at all.
   *
   * No `database`. This store has none, and saying so would be worse than
   * silence — a Feature that requires PostgreSQL is one this store genuinely
   * cannot take, and KNIGHT refusing it for an unreported engine is the right
   * answer arrived at honestly.
   */
  runtime() {
    return { name: 'node', node: process.versions.node };
  }

  async heartbeat({ status = 'Healthy', features = [], detail = null } = {}) {
    return this.#request('POST', '/api/v1/ingest/heartbeat', {
      environment: this.settings.environment,
      status,
      storeVersion: this.settings.storeVersion,
      dependencies: {},
      runtime: this.runtime(),
      features,
      detail,
    });
  }

  /** Claims this store's next job, or null. 204 is the common answer, not an error. */
  async claimJob() {
    const job = await this.#request('POST', '/api/v1/ingest/jobs/next');

    return job && Object.keys(job).length ? job : null;
  }

  /**
   * Reports one step's outcome.
   *
   * Safe to call twice for the same step: KNIGHT updates it in place rather than
   * appending, because an agent that finished a step and lost the reply will
   * report it again, and treating the repeat as a second execution would be a
   * job that ran a migration twice.
   */
  async reportStep(jobId, step, status, { output = null, errorCode = null, durationMs = null } = {}) {
    await this.#request('POST', `/api/v1/ingest/jobs/${jobId}/steps`, {
      step,
      status,
      output,
      errorCode,
      durationMilliseconds: durationMs,
    });
  }

  async completeJob(jobId, { succeeded, failureCode = null, failureMessage = null, installedVersion = null, health = null }) {
    await this.#request('POST', `/api/v1/ingest/jobs/${jobId}/complete`, {
      succeeded,
      failureCode,
      failureMessage,
      rollbackOutcome: null,
      installedVersion,
      health,
    });
  }

  async #request(method, path, body = null, { authenticated = true, retryOn401 = true } = {}) {
    if (authenticated && !this.token) {
      await this.handshake();
    }

    const headers = { Accept: 'application/json' };

    if (body !== null) {
      headers['Content-Type'] = 'application/json';
    }

    if (authenticated) {
      headers.Authorization = `Bearer ${this.token}`;
    }

    let response;

    try {
      response = await fetch(`${this.settings.baseUrl}${path}`, {
        method,
        headers,
        body: body === null ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(this.settings.timeoutMs),
      });
    } catch (error) {
      throw new KnightUnavailable(`${method} ${path} could not be reached: ${error.message}`);
    }

    if (response.status === 401 && authenticated && retryOn401) {
      // Expired, or minted before a credential rotation. Handshake and try once.
      this.token = null;
      await this.handshake();

      return this.#request(method, path, body, { authenticated, retryOn401: false });
    }

    if (response.status === 204 || response.headers.get('content-length') === '0') {
      return null;
    }

    const text = await response.text();

    if (!response.ok) {
      let detail = text.slice(0, 300);
      let code = '';

      try {
        const problem = JSON.parse(text);
        detail = problem.detail || problem.title || detail;
        code = problem.errorCode || '';
      } catch {
        // A body that is not a problem document is still a body worth showing.
      }

      throw new KnightRejected(response.status, detail, code);
    }

    return text ? JSON.parse(text) : null;
  }
}
