"""
Settings for the KNIGHT reference store.

This is a customer store: an independent Django application with its own
database and its own deployment. KNIGHT manages it, observes it and bills for
it, and is never its backend. Nothing in this file reaches into KNIGHT's
database, and nothing in KNIGHT reaches into this one.

Everything environment-specific is read from the environment. The KNIGHT client
secret in particular is read here and never written anywhere else — not into a
fixture, not into a settings file, not into the repository.
"""

import os
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent


def _env(name: str, default: str = "") -> str:
    return os.environ.get(name, default).strip()


def _flag(name: str, default: bool = False) -> bool:
    raw = _env(name)
    if not raw:
        return default
    return raw.lower() in {"1", "true", "yes", "on"}


def _int(name: str, default: int) -> int:
    try:
        return int(_env(name) or default)
    except ValueError:
        return default


# --- Django core ------------------------------------------------------------

SECRET_KEY = _env("DJANGO_SECRET_KEY", "development-only-store-secret-key-change-me")
DEBUG = _flag("DJANGO_DEBUG", True)
ALLOWED_HOSTS = [host for host in _env("DJANGO_ALLOWED_HOSTS", "*").split(",") if host]

INSTALLED_APPS = [
    "django.contrib.contenttypes",
    "django.contrib.auth",
    "django.contrib.staticfiles",
    "rest_framework",
    # The integration layer is an app of its own, and one the business apps
    # below never import from except through its published façade.
    "knight_integration",
    "apps.shop",
]

MIDDLEWARE = [
    "django.middleware.common.CommonMiddleware",
    # Last in the list so it sees exceptions from everything in front of it.
    # It reports and re-raises: reporting an error must never change how the
    # store answers the shopper who hit it.
    "knight_integration.errors.middleware.KnightErrorReportingMiddleware",
]

ROOT_URLCONF = "config.urls"
WSGI_APPLICATION = "config.wsgi.application"

TEMPLATES = [
    {
        "BACKEND": "django.template.backends.django.DjangoTemplates",
        "DIRS": [],
        "APP_DIRS": True,
        "OPTIONS": {"context_processors": []},
    },
]

DATABASES = {
    "default": {
        "ENGINE": "django.db.backends.postgresql",
        "NAME": _env("STORE_DB_NAME", "refstore"),
        "USER": _env("STORE_DB_USER", "knight"),
        "PASSWORD": _env("STORE_DB_PASSWORD", "knight"),
        "HOST": _env("STORE_DB_HOST", "127.0.0.1"),
        "PORT": _env("STORE_DB_PORT", "5433"),
    }
}

# Redis where one is configured, local memory where none is. The store's own
# cache holds the KNIGHT token and the entitlement set, so it must exist — but
# requiring Redis to run a store on a laptop would be a tax on nothing
# (docs/adr/0020-store-ingestion-authentication.md).
_redis_url = _env("KNIGHT_REDIS_URL")
CACHES = {
    "default": (
        {"BACKEND": "django.core.cache.backends.redis.RedisCache", "LOCATION": _redis_url}
        if _redis_url
        else {"BACKEND": "django.core.cache.backends.locmem.LocMemCache", "LOCATION": "knight-store"}
    )
}

DEFAULT_AUTO_FIELD = "django.db.models.BigAutoField"
USE_TZ = True
TIME_ZONE = "UTC"
STATIC_URL = "static/"

LOGGING = {
    "version": 1,
    "disable_existing_loggers": False,
    "formatters": {"plain": {"format": "%(asctime)s %(levelname)-7s %(name)s %(message)s"}},
    "handlers": {"console": {"class": "logging.StreamHandler", "formatter": "plain"}},
    "loggers": {
        "knight_integration": {"handlers": ["console"], "level": _env("KNIGHT_LOG_LEVEL", "INFO")},
        "django.request": {"handlers": ["console"], "level": "ERROR"},
    },
}

# --- KNIGHT integration -----------------------------------------------------
#
# Read here, validated in knight_integration.conf, and used nowhere else. The
# names match docs/store-integration.md §7.

KNIGHT = {
    "BASE_URL": _env("KNIGHT_BASE_URL", "http://localhost:5008"),
    "CLIENT_ID": _env("KNIGHT_CLIENT_ID"),
    "CLIENT_SECRET": _env("KNIGHT_CLIENT_SECRET"),
    "ENVIRONMENT": _env("KNIGHT_ENVIRONMENT", "Development"),
    "STORE_ID": _env("KNIGHT_STORE_ID"),
    "STORE_VERSION": _env("STORE_VERSION", "1.0.0"),
    "ERROR_REPORTING": _flag("KNIGHT_ERROR_REPORTING", True),
    "LOG_SHIPPING": _flag("KNIGHT_LOG_SHIPPING", False),
    "FEATURE_REFRESH_SECONDS": _int("KNIGHT_FEATURE_REFRESH_SECONDS", 300),
    "TIMEOUT_SECONDS": _int("KNIGHT_TIMEOUT_SECONDS", 5),
    # How long the last known good entitlement set may still be enforced after
    # it goes stale, while KNIGHT cannot be reached. Past this the store falls
    # back to the minimum safe set rather than keeping paid features on forever.
    "ENTITLEMENT_GRACE_SECONDS": _int("KNIGHT_ENTITLEMENT_GRACE_SECONDS", 86400),
    "ERROR_BATCH_SIZE": _int("KNIGHT_ERROR_BATCH_SIZE", 20),
    "ERROR_QUEUE_LIMIT": _int("KNIGHT_ERROR_QUEUE_LIMIT", 500),
    "ERROR_FLUSH_SECONDS": _int("KNIGHT_ERROR_FLUSH_SECONDS", 10),
    # Published so KNIGHT can prove the domain belongs to this store. Not a
    # secret — the whole point is that it is served publicly.
    "DOMAIN_VERIFICATION_TOKEN": _env("KNIGHT_DOMAIN_VERIFICATION_TOKEN"),
    # Tolerance for clock skew when checking a signed request from KNIGHT.
    "REQUEST_SIGNATURE_SKEW_SECONDS": _int("KNIGHT_REQUEST_SIGNATURE_SKEW_SECONDS", 300),
}
