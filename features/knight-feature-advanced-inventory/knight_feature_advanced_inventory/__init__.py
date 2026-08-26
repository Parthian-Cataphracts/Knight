"""
`advanced-inventory` — stock as a ledger.

The published surface is `services`. Nothing outside this package should import
its models: what they are is this Feature's business, and what they mean is the
service functions' (docs/feature-authoring.md section 3).
"""

default_app_config = "knight_feature_advanced_inventory.apps.AdvancedInventoryConfig"
