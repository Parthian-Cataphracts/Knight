from django.apps import AppConfig


class AdvancedInventoryConfig(AppConfig):
    """
    The KNIGHT hook.

    The label is fixed here rather than inferred, because it ends up in the
    migration table: letting Django derive it from the module name would mean a
    later rename silently orphaning every migration this feature has applied.
    """

    name = "knight_feature_advanced_inventory"
    label = "knight_inventory"
    verbose_name = "KNIGHT Advanced Inventory"
    default_auto_field = "django.db.models.BigAutoField"

    def ready(self) -> None:
        """
        Registers the `__trigram_similar` lookup the SKU and supplier lookups use.

        Django registers it in `django.contrib.postgres`'s own AppConfig, and a
        store is not required to have that app installed — the reference one does
        not. A feature may not edit a store's INSTALLED_APPS, so it registers what
        it uses itself, which is what `register_lookup` is public API for.
        `advanced-search` does the same thing for the same reason.
        """
        from django.contrib.postgres.lookups import TrigramSimilar
        from django.db.models import CharField

        CharField.register_lookup(TrigramSimilar)
