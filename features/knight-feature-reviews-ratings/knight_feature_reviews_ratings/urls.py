"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import product_reviews, product_reviews_json, submit_review

urlpatterns = [
    path("product/<int:product_id>/", product_reviews, name="knight-reviews-page"),
    path("product/<int:product_id>/data/", product_reviews_json, name="knight-reviews-data"),
    path("product/<int:product_id>/submit/", submit_review, name="knight-reviews-submit"),
]
