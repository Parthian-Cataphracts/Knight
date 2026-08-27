"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from . import views

urlpatterns = [
    path("", views.index, name="knight-marketplaces-index"),
    path("queue/", views.queue, name="knight-marketplaces-queue"),
    path("stuck/", views.stuck, name="knight-marketplaces-stuck"),
    path("differences/", views.differences, name="knight-marketplaces-differences"),
    path("webhooks/<slug:slug>/", views.webhook, name="knight-marketplaces-webhook"),
    path("messages/<int:message_id>/replay/", views.replay, name="knight-marketplaces-replay"),
    path("<slug:slug>/", views.connection, name="knight-marketplaces-connection"),
]
