"""
The initial schema.

CreateModel operations and an index, and nothing else. That is what lets the
manifest declare `reversible: true` honestly: Django can reverse all of these, so
a failed upgrade can put this store's schema back
(docs/adr/0016-feature-migration-and-removal-policy.md).
"""

from django.db import migrations, models


class Migration(migrations.Migration):
    initial = True

    dependencies = []

    operations = [
        migrations.CreateModel(
            name="AnalyticsEvent",
            fields=[
                ("id", models.BigAutoField(auto_created=True, primary_key=True, serialize=False)),
                ("name", models.CharField(db_index=True, max_length=100)),
                ("occurred_at", models.DateTimeField(db_index=True)),
                ("payload", models.JSONField(blank=True, default=dict)),
            ],
            options={"db_table": "knight_analytics_event", "ordering": ["-occurred_at"]},
        ),
        migrations.CreateModel(
            name="DailyRollup",
            fields=[
                ("id", models.BigAutoField(auto_created=True, primary_key=True, serialize=False)),
                ("day", models.DateField(db_index=True)),
                ("name", models.CharField(max_length=100)),
                ("count", models.PositiveIntegerField(default=0)),
            ],
            options={
                "db_table": "knight_analytics_daily_rollup",
                "ordering": ["-day", "name"],
                "unique_together": {("day", "name")},
            },
        ),
        migrations.AddIndex(
            model_name="analyticsevent",
            index=models.Index(fields=["name", "occurred_at"], name="knight_anal_name_occ_idx"),
        ),
    ]
