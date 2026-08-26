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
