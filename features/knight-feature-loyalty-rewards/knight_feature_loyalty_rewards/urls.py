"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import balance, history, programme

urlpatterns = [
    path("", programme, name="knight-loyalty-programme"),
    path("<str:subject>/", balance, name="knight-loyalty-balance"),
    path("<str:subject>/history/", history, name="knight-loyalty-history"),
]
