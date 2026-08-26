from django.apps import AppConfig


class LoyaltyRewardsConfig(AppConfig):
    """
    The KNIGHT hook.

    The label is fixed here rather than inferred, because it ends up in the
    migration table: letting Django derive it from the module name would mean a
    later rename silently orphaning every migration this feature has applied.
    """

    name = "knight_feature_loyalty_rewards"
    label = "knight_loyalty"
    verbose_name = "KNIGHT Loyalty and Rewards"
    default_auto_field = "django.db.models.BigAutoField"
