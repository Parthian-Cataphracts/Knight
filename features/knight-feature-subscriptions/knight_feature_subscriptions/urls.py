"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from . import views

urlpatterns = [
    path("", views.index, name="knight-subscriptions-index"),
    path("due/", views.due, name="knight-subscriptions-due"),
    path("configuration/", views.configuration, name="knight-subscriptions-configuration"),
    # Last, so that a reference can never shadow one of the fixed routes above.
    path("<str:reference>/", views.detail, name="knight-subscriptions-detail"),
    path("<str:reference>/pause/", views.pause, name="knight-subscriptions-pause"),
    path("<str:reference>/resume/", views.resume, name="knight-subscriptions-resume"),
    path("<str:reference>/cancel/", views.cancel, name="knight-subscriptions-cancel"),
]
