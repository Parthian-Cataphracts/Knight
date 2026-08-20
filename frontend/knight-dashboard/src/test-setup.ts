import "@testing-library/jest-dom/vitest";
import { vi } from "vitest";

/**
 * Component tests have no hub to connect to.
 *
 * Stubbed globally rather than per test because the connection is opened by
 * whichever component happens to subscribe first, so any test that renders a
 * live screen would otherwise spend its time failing to negotiate — and print
 * the failure into every unrelated test's output.
 *
 * The stub is faithful in the way that matters: subscribing returns an
 * unsubscribe, and nothing ever fires. A screen must be correct when no push
 * ever arrives, because that is exactly what a customer with a blocked
 * websocket experiences.
 */
vi.mock("@/lib/realtime/connection", () => ({
  connectRealtime: () => null,
  disconnectRealtime: () => undefined,
  isRealtimeConnected: () => false,
  onRealtime: () => () => undefined,
}));
