"""Generated service for Analytics Reports. Security, event recording and the dashboard come
from knight_service_kit; declare only what is specific to this Feature here."""

from knight_service_kit import create_app, DEFAULT_EVENTS

app = create_app("Analytics Reports", DEFAULT_EVENTS, env_prefix="ANALYTICS_REPORTS", version="2.0.0")
