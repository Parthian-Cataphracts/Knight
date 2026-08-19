from django.apps import AppConfig


class AnalyticsCoreConfig(AppConfig):
    """
    The KNIGHT hook.

    The app label is fixed here rather than inferred, because it ends up in the
    migration table: letting Django derive it from the module name would mean a
    later rename silently orphaning every migration this feature has applied.
    """

    name = "knight_feature_analytics_core"
    label = "knight_analytics_core"
    verbose_name = "KNIGHT Analytics Core"
    default_auto_field = "django.db.models.BigAutoField"
