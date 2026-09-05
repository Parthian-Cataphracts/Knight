import { useEffect, useRef } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { onRealtime, type RealtimeEvent } from "./connection";

/**
 * Subscribes to a realtime event for as long as a component is mounted.
 *
 * The handler is held in a ref so that subscribing does not depend on it being
 * referentially stable. Without that, a handler written inline — which is how
 * every caller writes one — would tear the hub subscription down and rebuild it
 * on every render, and a push arriving in that gap would be lost.
 */
export function useRealtime<T>(event: RealtimeEvent, handler: (payload: T) => void): void {
  const latest = useRef(handler);

  // Kept current in an effect rather than during render: a ref written while
  // rendering is read before commit and is what the rules-of-hooks refs check
  // exists to catch. The subscription still reads `latest.current`, so it always
  // calls the newest handler without re-subscribing.
  useEffect(() => {
    latest.current = handler;
  });

  useEffect(() => onRealtime<T>(event, (payload) => latest.current(payload)), [event]);
}

/**
 * Refetches the given collections whenever one of these events arrives.
 *
 * The push is treated as "your data is stale", never as the data itself. That
 * distinction is what keeps a screen correct for somebody who had the tab
 * closed: they missed the event, and the fetch does not care.
 *
 * Refetches are coalesced onto a short timer, because a job reporting nine steps
 * in quick succession should cost one request, not nine.
 */
export function useRealtimeRefresh(events: RealtimeEvent[], pathPrefixes: string[]): void {
  const client = useQueryClient();
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const prefixes = pathPrefixes.join("|");

  useEffect(() => {
    const refresh = () => {
      if (timer.current !== null) clearTimeout(timer.current);

      timer.current = setTimeout(() => {
        void client.invalidateQueries({
          predicate: (query) =>
            typeof query.queryKey[1] === "string" &&
            prefixes.split("|").some((prefix) => (query.queryKey[1] as string).startsWith(prefix)),
        });
      }, 400);
    };

    const unsubscribes = events.map((event) => onRealtime(event, refresh));

    return () => {
      if (timer.current !== null) clearTimeout(timer.current);
      unsubscribes.forEach((off) => off());
    };
  }, [client, events, prefixes]);
}
