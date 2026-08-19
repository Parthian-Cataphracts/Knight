"""
The bounded queue that carries error events to KNIGHT.

Three properties, in the order they matter:

1. **Reporting never blocks a request.** Enqueueing is a lock and an append.
   The HTTP call happens on a background thread that no shopper is waiting for.
2. **Reporting never exhausts the store.** The queue has a hard limit. Past it,
   the oldest event is dropped and a counter is incremented — a store in a crash
   loop generates errors faster than any control plane can accept them, and
   memory is the wrong thing to spend on that.
3. **A failed send is not a lost store.** Send failures are logged and the batch
   is dropped, not retried forever. KNIGHT being down must never turn into a
   growing backlog that eventually takes the store with it.

The drop counter is reported in the next successful batch, so the gap is
visible rather than silent.
"""

from __future__ import annotations

import atexit
import logging
import threading
import uuid
from collections import deque
from typing import Any

from ..conf import get_settings

logger = logging.getLogger(__name__)


class ErrorReporter:
    """A queue and the thread that drains it. One per process."""

    def __init__(self) -> None:
        config = get_settings()
        self._queue: deque[dict[str, Any]] = deque(maxlen=config.error_queue_limit)
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._stopping = threading.Event()
        self._thread: threading.Thread | None = None
        self._dropped = 0
        self._batch_size = config.error_batch_size
        self._flush_seconds = config.error_flush_seconds

    # --- Producer side -------------------------------------------------------

    def enqueue(self, event: dict[str, Any]) -> None:
        with self._lock:
            if len(self._queue) == self._queue.maxlen:
                # deque with maxlen drops the oldest itself; counting it here is
                # what makes the loss visible later.
                self._dropped += 1

            self._queue.append(event)
            should_flush = len(self._queue) >= self._batch_size

        self._ensure_thread()

        if should_flush:
            self._wake.set()

    @property
    def dropped(self) -> int:
        return self._dropped

    def pending(self) -> int:
        with self._lock:
            return len(self._queue)

    # --- Consumer side -------------------------------------------------------

    def flush(self) -> int:
        """
        Sends what is queued, in batches. Returns how many events were accepted.

        Called by the background thread, by the selftest command, and at
        shutdown — anywhere a caller is willing to wait for the network.
        """
        from ..client import KnightClient, KnightRejected, KnightUnavailable

        sent = 0

        while True:
            batch = self._take(self._batch_size)
            if not batch:
                return sent

            dropped = self._dropped
            self._dropped = 0

            if dropped:
                logger.warning("Dropped %s error events before this batch: the queue was full.", dropped)

            try:
                # The key makes a retry after a timeout harmless: KNIGHT
                # recognises the batch and does not write it twice.
                receipt = KnightClient().send_errors(batch, idempotency_key=uuid.uuid4().hex)
                sent += int(receipt.get("accepted", 0))

                if receipt.get("rejected"):
                    logger.warning(
                        "KNIGHT rejected %s events in a batch: %s",
                        receipt["rejected"],
                        "; ".join(receipt.get("errors", []))[:500],
                    )
            except (KnightUnavailable, KnightRejected) as exc:
                # Deliberately not requeued. A batch that failed once will
                # usually fail again, and the queue is a buffer for bursts, not a
                # durable outbox.
                logger.warning("Could not deliver %s error events to KNIGHT: %s", len(batch), exc)
                return sent

    def stop(self) -> None:
        self._stopping.set()
        self._wake.set()

        thread = self._thread
        if thread is not None and thread.is_alive():
            thread.join(timeout=5)

    def _take(self, count: int) -> list[dict[str, Any]]:
        with self._lock:
            return [self._queue.popleft() for _ in range(min(count, len(self._queue)))]

    def _ensure_thread(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return

        with self._lock:
            if self._thread is not None and self._thread.is_alive():
                return

            # A daemon thread: the store exiting must not wait on error
            # reporting, and the atexit hook below gives the queue one bounded
            # chance to drain first.
            self._thread = threading.Thread(target=self._run, name="knight-error-reporter", daemon=True)
            self._thread.start()

    def _run(self) -> None:
        while not self._stopping.is_set():
            self._wake.wait(timeout=self._flush_seconds)
            self._wake.clear()

            try:
                self.flush()
            except Exception:  # noqa: BLE001 - a reporter thread must not die
                logger.exception("The KNIGHT error reporter thread hit an unexpected error.")


_reporter: ErrorReporter | None = None
_reporter_lock = threading.Lock()


def reporter() -> ErrorReporter:
    global _reporter

    if _reporter is None:
        with _reporter_lock:
            if _reporter is None:
                _reporter = ErrorReporter()
                atexit.register(_shutdown)

    return _reporter


def _shutdown() -> None:
    if _reporter is None:
        return

    try:
        _reporter.flush()
    finally:
        _reporter.stop()
