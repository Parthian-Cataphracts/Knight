"""
Fuzzy matching, three phases after it was first wanted.

`pg_trgm` is a `CREATE EXTENSION`, which is neither a change to this Feature's
own tables nor something a rollback may undo: another Feature installed in the
same database may have started using it in the meantime. That is why 1.0 shipped
with a prefix pass and no typo tolerance, and it is what
docs/adr/0031-database-extensions-are-declared-not-migrated.md settles.

The extension is declared in the manifest, which is what KNIGHT validates and
what the store's `create-extensions` step acts on before this migration runs.
The statement below is the same one, run again: idempotent, and the reason it is
here at all is every path that has no installer in it - `manage.py migrate` on a
developer's checkout, and Django's test database.

Its reverse is a deliberate no-op. `DROP EXTENSION` is the operation this whole
decision exists to forbid.
"""

import django.contrib.postgres.indexes
from django.db import migrations


class Migration(migrations.Migration):

    dependencies = [
        ("knight_search", "0001_initial"),
    ]

    operations = [
        migrations.RunSQL(
            sql='CREATE EXTENSION IF NOT EXISTS "pg_trgm"',
            reverse_sql=migrations.RunSQL.noop,
        ),
        migrations.AddIndex(
            model_name="searchdocument",
            index=django.contrib.postgres.indexes.GinIndex(
                fields=["title"],
                name="knight_search_title_trgm",
                opclasses=["gin_trgm_ops"],
            ),
        ),
    ]
