"""
The subscriptions service.

Not a store, and not a Feature package. This is an ordinary Django application
that happens to be the thing behind `subscriptions` 2.0.0 in KNIGHT's catalogue
(``docs/adr/0033-api-driven-features.md``).

The distinction that matters, and the reason this file is short: **it owns its
own database**. In 1.x this same domain ran inside each store, holding that
store's database handle and adding its tables to that store's schema. Here it
holds its own, and a store cannot reach it except over HTTP with a signature.

One deployment serves every store. That has one consequence which shapes the
whole of ``subscriptions/models.py``: every row belongs to a store, and no query
may ever cross that line.
"""

from __future__ import annotations

import os
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent


def _flag(name: str, default: str = "false") -> bool:
    return os.environ.get(name, default).strip().lower() in {"1", "true", "yes", "on"}


# A generated default so a developer can start the service without ceremony, and
# a hard refusal in production so nobody ships the generated one. The store's own
# settings make the same call for the same reason.
SECRET_KEY = os.environ.get("SUBSCRIPTIONS_SECRET_KEY", "")
DEBUG = _flag("SUBSCRIPTIONS_DEBUG", "false")

if not SECRET_KEY:
    if not DEBUG:
        raise RuntimeError(
            "SUBSCRIPTIONS_SECRET_KEY is not set. Set it, or set SUBSCRIPTIONS_DEBUG=true "
            "for local work."
        )

    SECRET_KEY = "development-only-not-for-anything-that-matters"

ALLOWED_HOSTS = [
    host.strip()
    for host in os.environ.get("SUBSCRIPTIONS_ALLOWED_HOSTS", "*").split(",")
    if host.strip()
]

INSTALLED_APPS = [
    "django.contrib.contenttypes",
    "django.contrib.auth",
    "knightlink",
    "subscriptions",
]

# No sessions, no CSRF, no auth middleware, and that is deliberate rather than an
# omission. Nothing here is reached by a browser: every request arrives from a
# store, is authenticated by an HMAC over its body, and carries the store's own
# assertion of who is asking. A session cookie on this service would be a second
# way in that nobody had thought about.
MIDDLEWARE = [
    "django.middleware.security.SecurityMiddleware",
    "django.middleware.common.CommonMiddleware",
]

ROOT_URLCONF = "config.urls"
WSGI_APPLICATION = "config.wsgi.application"

TEMPLATES = [
    {
        "BACKEND": "django.template.backends.django.DjangoTemplates",
        "DIRS": [],
        "APP_DIRS": False,
        "OPTIONS": {"context_processors": []},
    }
]

DATABASES = {
    "default": {
        "ENGINE": "django.db.backends.postgresql",
        "NAME": os.environ.get("SUBSCRIPTIONS_DB_NAME", "subscriptions"),
        "USER": os.environ.get("SUBSCRIPTIONS_DB_USER", "knight"),
        "PASSWORD": os.environ.get("SUBSCRIPTIONS_DB_PASSWORD", "knight"),
        "HOST": os.environ.get("SUBSCRIPTIONS_DB_HOST", "127.0.0.1"),
        "PORT": os.environ.get("SUBSCRIPTIONS_DB_PORT", "5433"),
        "CONN_MAX_AGE": int(os.environ.get("SUBSCRIPTIONS_DB_CONN_MAX_AGE", "60")),
    }
}

DEFAULT_AUTO_FIELD = "django.db.models.BigAutoField"

LANGUAGE_CODE = "en-us"
TIME_ZONE = "UTC"
USE_I18N = False
USE_TZ = True

# --- The store contract -----------------------------------------------------

#: How far a store's clock may be out before a signed request is refused.
#:
#: The store sends the value it used, and this is the ceiling on what will be
#: accepted, so a store cannot widen its own window by claiming a longer one.
KNIGHT_MAX_SKEW_SECONDS = int(os.environ.get("SUBSCRIPTIONS_MAX_SKEW_SECONDS", "300"))

#: How long a used nonce is remembered. Must be at least the skew window, or a
#: request could be replayed after its nonce was forgotten and while its
#: timestamp was still acceptable — which would leave the replay defence with a
#: hole exactly as wide as the difference.
#: The secret KNIGHT itself signs with, for the endpoints that say who the
#: stores are and what they may sign with.
#:
#: One secret, and it is not any store's. A store cannot prove it is a store
#: before it has a secret, and issuing that secret is exactly what these
#: endpoints do — so the control plane needs a credential of its own or the
#: registration path is circular. Unset means the control-plane surface refuses
#: everything, which is the only safe default for the one caller that can issue
#: a credential.
KNIGHT_CONTROL_SECRET = os.environ.get("SUBSCRIPTIONS_CONTROL_SECRET", "")

#: How long a store's previous secret keeps working after a rotation, when
#: KNIGHT does not say. Long enough that a store which has not yet picked up its
#: new configuration is not cut off, short enough that a leaked secret is not
#: valid all afternoon.
KNIGHT_DEFAULT_OVERLAP_SECONDS = int(
    os.environ.get("SUBSCRIPTIONS_SECRET_OVERLAP_SECONDS", "3600")
)

KNIGHT_NONCE_TTL_SECONDS = max(
    KNIGHT_MAX_SKEW_SECONDS * 2,
    int(os.environ.get("SUBSCRIPTIONS_NONCE_TTL_SECONDS", "900")),
)

LOGGING = {
    "version": 1,
    "disable_existing_loggers": False,
    "formatters": {"plain": {"format": "%(asctime)s %(levelname)-7s %(name)s %(message)s"}},
    "handlers": {"console": {"class": "logging.StreamHandler", "formatter": "plain"}},
    "root": {"handlers": ["console"], "level": os.environ.get("SUBSCRIPTIONS_LOG_LEVEL", "INFO")},
}
