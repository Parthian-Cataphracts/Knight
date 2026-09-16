"""Generated service for Advanced Search. Security, event recording and the dashboard come
from knight_service_kit; declare only what is specific to this Feature here."""

from knight_service_kit import create_app, DEFAULT_EVENTS

app = create_app("Advanced Search", DEFAULT_EVENTS, env_prefix="ADVANCED_SEARCH", version="2.0.0")
