"""
The store's own tables for its KNIGHT integration.

One, at the moment: the queue that carries an event to a Feature's service.
Everything else about the integration is either configuration or a JSON registry
on disk, deliberately — a store must be able to read what it has installed
during an incident without a database being up.

A delivery is different: it is state that has to survive a restart, be locked by
exactly one worker at a time, and be queried by "what is due". That is a table.
"""

from .external.delivery import DeliveryState, WebhookDelivery

__all__ = ["DeliveryState", "WebhookDelivery"]
