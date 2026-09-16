"""Generated service for Loyalty and Rewards. Security, event recording and the dashboard come
from knight_service_kit; declare only what is specific to this Feature here."""

from knight_service_kit import create_app, DEFAULT_EVENTS

app = create_app("Loyalty and Rewards", DEFAULT_EVENTS, env_prefix="LOYALTY_REWARDS", version="2.0.0")
