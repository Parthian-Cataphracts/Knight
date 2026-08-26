"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import check, credit

urlpatterns = [
    path("check/", check, name="knight-gift-card-check"),
    path("credit/<str:subject>/", credit, name="knight-store-credit"),
]
