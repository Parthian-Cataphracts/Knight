"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from . import views

urlpatterns = [
    path("", views.places, name="knight-locations-places"),
    path("route/", views.route, name="knight-locations-route"),
    path("orders/<int:number>/", views.routing, name="knight-locations-routing"),
    # Last, so that a location code can never shadow one of the fixed routes.
    path("<str:code>/", views.place, name="knight-locations-place"),
    path("<str:code>/roster/", views.roster, name="knight-locations-roster"),
    path("<str:code>/menu/", views.menu, name="knight-locations-menu"),
]
