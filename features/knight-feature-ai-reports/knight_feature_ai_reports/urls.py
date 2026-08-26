"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import configuration, history, latest, usage

urlpatterns = [
    path("", latest, name="knight-ai-reports-latest"),
    path("history/", history, name="knight-ai-reports-history"),
    path("usage/", usage, name="knight-ai-reports-usage"),
    path("configuration/", configuration, name="knight-ai-reports-configuration"),
]
