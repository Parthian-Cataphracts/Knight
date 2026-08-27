"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from . import views

urlpatterns = [
    path("", views.board, name="knight-restaurant-board"),
    path("floor/", views.floor, name="knight-restaurant-floor"),
    path("load/", views.load, name="knight-restaurant-load"),
    path("slots/", views.slots, name="knight-restaurant-slots"),
    path("seat/", views.seat, name="knight-restaurant-seat"),
    path("clear/", views.clear, name="knight-restaurant-clear"),
    path("book/", views.book, name="knight-restaurant-book"),
    # Last, so that a ticket number can never shadow one of the fixed routes.
    path("<int:number>/", views.ticket, name="knight-restaurant-ticket"),
    path("<int:number>/advance/", views.advance, name="knight-restaurant-advance"),
]
