"""
`drill` — the Feature the delivery drill moves up and down.

Not in the commercial catalogue and never for sale. It exists so that an upgrade
and a rollback in the drill are **real schema changes**: 1.1.0 has a column 1.0.0
does not, so rolling back has something to reverse and the rows have something to
survive.

The two versions are two source trees rather than one tree with a build-time
switch, deliberately. A reader can diff them and see the whole difference, and
neither has to carry a model field its own migrations do not create — which is
not a hypothetical: a package whose model knows about a column its schema lacks
fails on the first insert, not at import, which is a bad way for a drill to fail.
"""

default_app_config = "knight_feature_drill.apps.DrillConfig"
