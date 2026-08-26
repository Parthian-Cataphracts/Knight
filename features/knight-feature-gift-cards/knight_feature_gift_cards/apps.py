from django.apps import AppConfig


class GiftCardsConfig(AppConfig):
    """
    The KNIGHT hook.

    The label is fixed here rather than inferred, because it ends up in the
    migration table: letting Django derive it from the module name would mean a
    later rename silently orphaning every migration this feature has applied.
    """

    name = "knight_feature_gift_cards"
    label = "knight_gift_cards"
    verbose_name = "KNIGHT Gift Cards and Store Credit"
    default_auto_field = "django.db.models.BigAutoField"
