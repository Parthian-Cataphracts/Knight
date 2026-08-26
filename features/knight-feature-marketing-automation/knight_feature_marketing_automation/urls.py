"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import configuration, history, overview, unsubscribe

urlpatterns = [
    path("", overview, name="knight-marketing-overview"),
    path("configuration/", configuration, name="knight-marketing-configuration"),
    path("unsubscribe/", unsubscribe, name="knight-marketing-unsubscribe"),
    path("<slug:slug>/history/", history, name="knight-marketing-history"),
]
