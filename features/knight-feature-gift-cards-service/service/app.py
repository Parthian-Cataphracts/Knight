"""Generated service for Gift Cards and Store Credit. Security, event recording and the dashboard come
from knight_service_kit; declare only what is specific to this Feature here."""

from knight_service_kit import create_app, DEFAULT_EVENTS

app = create_app("Gift Cards and Store Credit", DEFAULT_EVENTS, env_prefix="GIFT_CARDS", version="2.0.0")
