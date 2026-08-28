"""
A store's shared secret becomes a row with a lifetime.

The column could hold one secret, so changing it was an outage: every request
signed with the old value and still in flight was refused the moment the new one
was written. Rotation is the reason the table exists.

The existing secret is carried across before the column is dropped, with no
expiry, so a service upgraded in place keeps answering every store it already
answered. A migration that dropped the column and left an operator to re-add
each store would be a migration that takes the fleet down.
"""

import django.db.models.deletion
import django.utils.timezone
from django.db import migrations, models


def carry_secrets_across(apps, schema_editor):
    """Every registered store keeps signing with what it signs with today."""
    Store = apps.get_model("knightlink", "Store")
    StoreSecret = apps.get_model("knightlink", "StoreSecret")

    for store in Store.objects.all().iterator():
        if not store.secret:
            continue

        StoreSecret.objects.get_or_create(
            store=store,
            secret=store.secret,
            defaults={"issued_by": "carried over from the store's secret column"},
        )


def put_them_back(apps, schema_editor):
    """
    The way back, for a rollback onto the older code.

    The newest usable secret wins, because that is the one the older column
    would have held — and a rollback that restored an expiring secret would
    leave a service verifying against a value KNIGHT has already replaced.
    """
    Store = apps.get_model("knightlink", "Store")

    for store in Store.objects.all().iterator():
        newest = (
            store.signing_secrets.filter(revoked_at__isnull=True)
            .order_by("-valid_from", "-id")
            .first()
        )

        if newest is not None:
            store.secret = newest.secret
            store.save(update_fields=["secret"])


class Migration(migrations.Migration):

    dependencies = [
        ('knightlink', '0001_initial'),
    ]

    operations = [
        migrations.CreateModel(
            name='StoreSecret',
            fields=[
                ('id', models.BigAutoField(auto_created=True, primary_key=True, serialize=False, verbose_name='ID')),
                ('secret', models.CharField(max_length=200)),
                ('valid_from', models.DateTimeField(default=django.utils.timezone.now)),
                ('expires_at', models.DateTimeField(blank=True, null=True)),
                ('revoked_at', models.DateTimeField(blank=True, null=True)),
                ('issued_by', models.CharField(blank=True, default='', max_length=200)),
                ('created_at', models.DateTimeField(auto_now_add=True)),
            ],
            options={
                'db_table': 'knight_store_secret',
                'ordering': ('-valid_from', '-id'),
            },
        ),
        migrations.AlterField(
            model_name='seennonce',
            name='store',
            field=models.ForeignKey(blank=True, null=True, on_delete=django.db.models.deletion.CASCADE, related_name='nonces', to='knightlink.store'),
        ),
        migrations.AddConstraint(
            model_name='seennonce',
            constraint=models.UniqueConstraint(condition=models.Q(('store__isnull', True)), fields=('nonce',), name='knight_control_nonce_once'),
        ),
        migrations.AddField(
            model_name='storesecret',
            name='store',
            field=models.ForeignKey(on_delete=django.db.models.deletion.CASCADE, related_name='signing_secrets', to='knightlink.store'),
        ),
        migrations.AddIndex(
            model_name='storesecret',
            index=models.Index(fields=['store', 'expires_at'], name='knight_secret_window'),
        ),
        migrations.AddConstraint(
            model_name='storesecret',
            constraint=models.UniqueConstraint(fields=('store', 'secret'), name='knight_secret_once'),
        ),

        # Only now, with somewhere for them to go.
        migrations.RunPython(carry_secrets_across, put_them_back),

        migrations.RemoveField(
            model_name='store',
            name='secret',
        ),
    ]
