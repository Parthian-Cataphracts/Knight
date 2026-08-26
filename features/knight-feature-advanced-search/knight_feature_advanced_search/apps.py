from django.apps import AppConfig


class AdvancedSearchConfig(AppConfig):
    """
    The KNIGHT hook.

    The label is fixed here rather than inferred, because it ends up in the
    migration table: letting Django derive it from the module name would mean a
    later rename silently orphaning every migration this feature has applied.
    """

    name = "knight_feature_advanced_search"
    label = "knight_search"
    verbose_name = "KNIGHT Advanced Search"
    default_auto_field = "django.db.models.BigAutoField"

    def ready(self) -> None:
        """
        Registers the `__trigram_similar` lookup this feature's typo pass uses.

        Django registers it in `django.contrib.postgres`'s own AppConfig, and a
        store is not required to have that app installed — the reference one does
        not. A feature may not edit a store's INSTALLED_APPS, so it registers
        what it uses itself, which is what `register_lookup` is public API for.

        Idempotent: registering the same lookup twice replaces it with itself,
        so a store that *does* install `django.contrib.postgres` is unaffected
        either way round.
        """
        from django.contrib.postgres.lookups import TrigramSimilar
        from django.db.models import CharField

        CharField.register_lookup(TrigramSimilar)
