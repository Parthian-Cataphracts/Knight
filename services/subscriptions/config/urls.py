"""
Everything this service answers.

Three groups, and the shape mirrors what `subscriptions` 2.0.0's manifest
declares, because the manifest is the contract and a service whose routes had
drifted from it would fail in the store rather than here:

- `/hooks/*`   — the four events the store forwards
- `/api/v1/*`  — what the store's two proxy prefixes forward to
- `/healthz`   — liveness, and the one route that is not signed

Nothing else. In particular there is no admin site, no login and no session
endpoint: identity is decided in the store and asserted here, and a second way
in would be a second thing to get wrong.
"""

from django.urls import path

from knightlink import views as knight_views
from subscriptions import views, webhooks

urlpatterns = [
    # Unsigned on purpose. A liveness probe that needed a credential could not be
    # run by the thing most likely to need it — a load balancer — and it reveals
    # nothing but whether the process is up.
    path("healthz", knight_views.healthz, name="healthz"),

    path("hooks/order-placed", webhooks.order_placed, name="hook-order-placed"),
    path("hooks/order-paid", webhooks.order_paid, name="hook-order-paid"),
    path("hooks/order-cancelled", webhooks.order_cancelled, name="hook-order-cancelled"),
    path("hooks/order-refunded", webhooks.order_refunded, name="hook-order-refunded"),

    # What `subscriptions/` forwards to.
    path("api/v1/subscriptions/", views.index, name="subscriptions"),
    path("api/v1/subscriptions/<str:reference>/", views.detail, name="subscription"),
    path("api/v1/subscriptions/<str:reference>/pause/", views.pause, name="subscription-pause"),
    path("api/v1/subscriptions/<str:reference>/resume/", views.resume, name="subscription-resume"),
    path("api/v1/subscriptions/<str:reference>/cancel/", views.cancel, name="subscription-cancel"),

    # What `admin/subscriptions/` forwards to.
    path("api/v1/admin/", views.admin_index, name="admin-subscriptions"),
    path("api/v1/admin/due/", views.admin_due, name="admin-due"),
    path("api/v1/admin/<str:reference>/", views.admin_detail, name="admin-subscription"),
]
