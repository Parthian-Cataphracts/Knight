"""
The credential in force, and where a rotated one is kept.

The store is issued a client id and secret and, until rotation, they arrived one
way only: the environment. That is still the floor. But KNIGHT can now rotate a
credential nearing expiry and hand the replacement back on the handshake that
used the old one (docs/hardening-backlog.md P2), and a replacement the store
throws away is a store that is locked out the moment the old secret's grace ends.

So a rotated credential is persisted — a small file beside the feature registry,
the same place delivered configuration secrets already live — and it takes
precedence over the environment from then on. The environment stays the floor:
the value an operator set is what a store connects with until KNIGHT rotates it,
and a store that has never rotated writes nothing here at all.
"""

from __future__ import annotations

import json
import logging
import os
from pathlib import Path
from typing import Any

from .conf import KnightSettings

logger = logging.getLogger(__name__)

_FILENAME = "knight-credential.json"


def _path(config: KnightSettings) -> Path:
    return Path(config.feature_root) / _FILENAME


def read_stored(config: KnightSettings) -> tuple[str, str] | None:
    """
    The persisted credential, or None when there is not a complete one.

    Unreadable is treated as absent rather than fatal: a store that refused to
    start because a credential file was truncated would be a shop that is down
    for a reason unrelated to selling anything. It falls back to the environment.
    """
    path = _path(config)

    if not path.exists():
        return None

    try:
        stored = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None

    client_id = str(stored.get("clientId", ""))
    client_secret = str(stored.get("clientSecret", ""))

    if not client_id or not client_secret:
        return None

    return client_id, client_secret


def active_credential(config: KnightSettings) -> tuple[str, str]:
    """
    The credential every handshake authenticates with: the persisted one when
    there is a complete one, the environment otherwise.
    """
    return read_stored(config) or (config.client_id, config.client_secret)


def save_rotated(config: KnightSettings, client_id: str, client_secret: str) -> None:
    """
    Persists a credential KNIGHT rotated, so every handshake from now on uses it.

    Written atomically and, where the platform supports it, readable only by this
    user — it holds a secret, and the one KNIGHT will never hand back again.
    """
    path = _path(config)
    path.parent.mkdir(parents=True, exist_ok=True)

    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps({"clientId": client_id, "clientSecret": client_secret}),
        encoding="utf-8",
    )

    if os.name == "posix":
        try:
            os.chmod(temporary, 0o600)
        except OSError:
            # Some mounts do not support it. The file still holds a secret, and
            # saying so is the host's business rather than a reason to refuse.
            pass

    os.replace(temporary, path)


def adopt_if_rotated(config: KnightSettings, handshake_body: dict[str, Any]) -> bool:
    """
    Adopts a rotated credential a handshake handed back, if it did.

    Returns whether one was adopted. The credential just used to authenticate
    keeps working through its grace window, so this store's current session stays
    valid; the next handshake picks up the replacement stored here.
    """
    rotated = handshake_body.get("rotatedCredential")

    if not isinstance(rotated, dict):
        return False

    client_id = str(rotated.get("clientId", ""))
    client_secret = str(rotated.get("clientSecret", ""))

    if not client_id or not client_secret:
        return False

    save_rotated(config, client_id, client_secret)
    logger.info("KNIGHT rotated this store's credential; adopted the replacement for the next handshake.")

    return True


def forget_stored(config: KnightSettings) -> None:
    """Removes a persisted credential, so the store falls back to the environment."""
    path = _path(config)

    if path.exists():
        path.unlink()
