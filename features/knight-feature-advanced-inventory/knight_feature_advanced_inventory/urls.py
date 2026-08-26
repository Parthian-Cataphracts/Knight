"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from . import views

urlpatterns = [
    path("", views.levels, name="knight-inventory-levels"),
    path("alerts/", views.alerts, name="knight-inventory-alerts"),
    path("reorder/", views.reorder, name="knight-inventory-reorder"),
    path("orders/", views.purchase_orders, name="knight-inventory-orders"),
    path("search/", views.search, name="knight-inventory-search"),
    path("stocktake/", views.stocktake, name="knight-inventory-stocktake"),
    path("receive/", views.receive, name="knight-inventory-receive"),
    # Last, so that a SKU can never shadow one of the fixed routes above.
    path("<str:sku>/", views.availability, name="knight-inventory-availability"),
    path("<str:sku>/history/", views.history, name="knight-inventory-history"),
]
