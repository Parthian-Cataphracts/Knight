"""Generated service for Reviews and Ratings. Security, event recording and the dashboard come
from knight_service_kit; declare only what is specific to this Feature here."""

from knight_service_kit import create_app, DEFAULT_EVENTS

app = create_app("Reviews and Ratings", DEFAULT_EVENTS, env_prefix="REVIEWS_RATINGS", version="2.0.0")
