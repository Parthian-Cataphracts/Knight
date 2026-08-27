"""Routes this feature contributes."""

from django.urls import path

from . import views

urlpatterns = [path("", views.index, name="knight-drill-index")]
