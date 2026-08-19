import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import { getAccessToken } from "@/lib/api/client";

/**
 * The realtime channel to the control plane.
 *
 * Two things about this are deliberate and worth keeping:
 *
 * * **The client never asks for anything.** There is no subscribe call, because
 *   the server places the connection into its groups from the claims on the
 *   token that authenticated it. A client that could name a group could name
 *   somebody else's (docs/authorization.md §3).
 * * **Nothing depends on it working.** Every screen fetches its own data and
 *   would still be correct if this connection never opened. Realtime is an
 *   improvement on polling, so a failure here is logged and dropped rather than
 *   surfaced as a broken page.
 */
const HUB_PATH = "/hubs/control-plane";

export type RealtimeEvent =
  | "notificationReceived"
  | "incidentOpened"
  | "incidentChanged"
  | "jobProgress"
  | "jobCompleted"
  | "featureInstallationStateChanged";

export interface RealtimeNotification {
  id: string;
  severity: "Info" | "Warning" | "Critical";
  ruleKey: string;
  title: string;
  body: string;
  subject: string;
  subjectId: string;
}

let connection: HubConnection | null = null;

function hubUrl(): string {
  const configured = import.meta.env.VITE_SIGNALR_URL;

  if (configured) return configured;

  // The hub is not under the versioned API prefix — it is a transport, not a
  // resource — so the prefix is stripped rather than appended to.
  const base = import.meta.env.VITE_API_BASE_URL ?? "/api/v1";

  return `${base.replace(/\/api\/v1\/?$/, "")}${HUB_PATH}`;
}

/**
 * Opens the connection, or returns the one already open.
 *
 * The token is supplied per negotiation rather than captured once, so a session
 * that refreshes mid-connection reconnects with the new token instead of being
 * silently dropped at the old one's expiry.
 */
export function connectRealtime(): HubConnection {
  if (connection) return connection;

  connection = new HubConnectionBuilder()
    .withUrl(hubUrl(), { accessTokenFactory: () => getAccessToken() ?? "" })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  connection.start().catch((error: unknown) => {
    // Deliberately not surfaced. The dashboard polls anyway; telling an
    // operator that a transport they never asked for is unavailable would be
    // noise during exactly the incident they opened the page for.
    console.warn("The realtime channel is unavailable; falling back to polling.", error);
  });

  return connection;
}

export function disconnectRealtime(): void {
  if (!connection) return;

  const closing = connection;
  connection = null;

  if (closing.state !== HubConnectionState.Disconnected) {
    void closing.stop();
  }
}

/** Subscribes to one event and answers the function that unsubscribes. */
export function onRealtime<T>(event: RealtimeEvent, handler: (payload: T) => void): () => void {
  const hub = connectRealtime();

  hub.on(event, handler as (...args: unknown[]) => void);

  return () => hub.off(event, handler as (...args: unknown[]) => void);
}
