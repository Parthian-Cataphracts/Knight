"""App configuration for the store's own business domain."""

from django.apps import AppConfig


class ShopConfig(AppConfig):
    name = "apps.shop"
    label = "shop"
    verbose_name = "Shop"
