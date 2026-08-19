"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import daily_summary

urlpatterns = [
    path("summary/", daily_summary, name="knight-analytics-summary"),
]
