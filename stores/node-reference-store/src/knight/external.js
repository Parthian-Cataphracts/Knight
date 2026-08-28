/**
 * Features that are services rather than packages.
 *
 * The store runs none of their code. What it holds for them is three things:
 * a list of its own events to forward, a set of route prefixes to proxy, and a
 * set of places their screens hang
 * (docs/adr/0033-api-driven-features.md).
 *
 * The store's half of the contract lives here too — the events it publishes and
 * the slots it offers. KNIGHT validates the *shape* of an event name at publish
 * because it cannot know what any particular store emits; the store validates
 * the *name* at install because it is the only thing that can. A Feature
 * subscribing to an event that does not exist would otherwise install cleanly,
 * pass its health check, and never hear anything.
 */

import { StepFailed } from './steps.js';

/** Business events this store emits, as `domain.thing_happened`. */
export const KNOWN_EVENTS = new Set([
  'order.placed',
  'order.paid',
  'order.cancelled',
  'order.refunded',
  'order.fulfilled',
  'cart.abandoned',
  'customer.registered',
  'customer.updated',
  'product.created',
  'product.updated',
  'product.stock_changed',
  'subscription.renewal_due',
]);

/** Where an external Feature's screens may appear. */
export const UI_SLOTS = new Set([
  'admin.sidebar',
  'admin.order_detail',
  'admin.customer_detail',
  'admin.settings',
  'storefront.account',
]);

/**
 * Whether this job delivers configuration rather than code.
 *
 * Read from the job, which KNIGHT fills in from the signed manifest, rather
 * than sniffed from the artifact. The agent has to know before it fetches: the
 * two architectures want the same bytes handled completely differently.
 */
export function isExternal(job) {
  return (job?.architecture ?? 'in_process') === 'external_service';
}

/**
 * The signed configuration document, parsed and checked against this store.
 *
 * Nothing here is trusted before its digest and its signature have been
 * checked — that ordering is the whole reason the configuration is signed at
 * all. Without it a store would wire a proxy route, carrying its customers'
 * requests, to whatever host answered the download URL.
 */
export function readContract(bytes, job) {
  let document;

  try {
    document = JSON.parse(Buffer.from(bytes).toString('utf8'));
  } catch (error) {
    throw new StepFailed('install.unreadable_config', `The configuration document could not be read: ${error.message}`);
  }

  if (document === null || typeof document !== 'object' || Array.isArray(document)) {
    throw new StepFailed('install.unreadable_config', 'The configuration document is not an object.');
  }

  if (document.architecture !== 'external_service') {
    // The job says one thing and the signed document says another. Acting on
    // either would be choosing which of two disagreeing sources to trust, and
    // the honest answer is neither.
    throw new StepFailed(
      'install.wrong_architecture',
      'The job says this Feature is an external service and the signed document does not agree.',
    );
  }

  const service = document.service ?? {};

  if (!service.base_url) {
    throw new StepFailed('install.no_service', 'The configuration names no service to talk to.');
  }

  const webhooks = document.webhooks ?? [];
  const apiProxies = document.api_proxies ?? [];
  const uiMounts = document.ui_mounts ?? [];

  for (const subscription of webhooks) {
    if (!KNOWN_EVENTS.has(subscription?.event)) {
      throw new StepFailed(
        'install.unknown_event',
        `${job.featureSlug} subscribes to '${subscription?.event}', which this store does not publish. ` +
          `Known events: ${[...KNOWN_EVENTS].sort().join(', ')}.`,
      );
    }
  }

  for (const mount of uiMounts) {
    if (!UI_SLOTS.has(mount?.slot)) {
      throw new StepFailed(
        'install.unknown_slot',
        `${job.featureSlug} hangs a screen in '${mount?.slot}', which this store does not offer. ` +
          `Known slots: ${[...UI_SLOTS].sort().join(', ')}.`,
      );
    }
  }

  return {
    architecture: 'external_service',
    service,
    webhooks,
    api_proxies: apiProxies,
    ui_mounts: uiMounts,
  };
}

/**
 * The subscribers for one event, from what this store has registered.
 *
 * Reads the registry every time rather than caching. A Feature disabled a
 * second ago must stop receiving events now, not at the next restart — an
 * entitlement that lapsed is a commercial fact and the store enforces it.
 */
export async function subscribersFor(registry, event) {
  const features = await registry.all();
  const subscribers = [];

  for (const feature of Object.values(features)) {
    if (!feature.enabled || feature.contract?.architecture !== 'external_service') {
      continue;
    }

    for (const subscription of feature.contract.webhooks ?? []) {
      if (subscription.event === event) {
        subscribers.push({ slug: feature.slug, contract: feature.contract, subscription });
      }
    }
  }

  return subscribers;
}
