"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import for_subject, members, overview

urlpatterns = [
    path("", overview, name="knight-segments"),
    path("<slug:slug>/members/", members, name="knight-segment-members"),
    path("subject/<str:subject>/", for_subject, name="knight-segments-for-subject"),
]
