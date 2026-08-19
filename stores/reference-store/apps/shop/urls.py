"""Storefront routes."""

from django.urls import path

from . import views

urlpatterns = [
    path("", views.catalogue, name="catalogue"),
    path("loyalty/", views.loyalty, name="loyalty"),
    path("boom/", views.boom, name="boom"),
]
