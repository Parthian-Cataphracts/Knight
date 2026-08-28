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

from knightlink import control, views as knight_views
from subscriptions import views, webhooks

urlpatterns = [
    # Unsigned on purpose. A liveness probe that needed a credential could not be
    # run by the thing most likely to need it — a load balancer — and it reveals
    # nothing but whether the process is up.
    path("healthz", knight_views.healthz, name="healthz"),

    # What KNIGHT may say, signed with the control plane's own secret rather
    # than a store's. Kept under its own prefix so the two callers are visible
    # in a route table: everything under `/knight/` is the control plane, and
    # nothing else is.
    path("knight/stores/register", control.register, name="control-register"),
    path("knight/stores/rotate", control.rotate, name="control-rotate"),
    path("knight/stores/revoke", control.revoke, name="control-revoke"),
    path("knight/stores/describe", control.describe, name="control-describe"),

    path("hooks/order-placed", webhooks.order_placed, name="hook-order-placed"),
    path("hooks/order-paid", webhooks.order_paid, name="hook-order-paid"),
    path("hooks/order-cancelled", webhooks.order_cancelled, name="hook-order-cancelled"),
    path("hooks/order-refunded", webhooks.order_refunded, name="hook-order-refunded"),

    # What `subscribe/` forwards to. Public, and signed like everything else.
    path("api/v1/public/", views.public, name="public"),

    # What `subscriptions/` forwards to.
    path("api/v1/subscriptions/", views.index, name="subscriptions"),
    path("api/v1/subscriptions/<str:reference>/", views.detail, name="subscription"),
    path("api/v1/subscriptions/<str:reference>/pause/", views.pause, name="subscription-pause"),
    path("api/v1/subscriptions/<str:reference>/resume/", views.resume, name="subscription-resume"),
    path("api/v1/subscriptions/<str:reference>/cancel/", views.cancel, name="subscription-cancel"),

    # What `admin/subscriptions/` forwards to.
    path("api/v1/admin/", views.admin_index, name="admin-subscriptions"),
    path("api/v1/admin/due/", views.admin_due, name="admin-due"),

    # The billing loop. Declared before `<str:reference>/` because a literal
    # route that came after a catch-all would be read as a subscription called
    # "awaiting-orders", which is the kind of thing that works until somebody
    # names one that.
    path("api/v1/admin/awaiting-orders/", views.awaiting_orders, name="admin-awaiting-orders"),
    path(
        "api/v1/admin/<str:reference>/periods/<int:sequence>/order/",
        views.record_order,
        name="admin-record-order",
    ),

    path("api/v1/admin/<str:reference>/", views.admin_detail, name="admin-subscription"),
]
