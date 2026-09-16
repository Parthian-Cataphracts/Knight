"""Generated service for Customer Segmentation. Security, event recording and the dashboard come
from knight_service_kit; declare only what is specific to this Feature here."""

from knight_service_kit import create_app, DEFAULT_EVENTS

app = create_app("Customer Segmentation", DEFAULT_EVENTS, env_prefix="CUSTOMER_SEGMENTATION", version="2.0.0")
