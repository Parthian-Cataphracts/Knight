"""
Tests for delivered Features, run against this store.

A Feature is tested as a normal Django app against a project that has it in
INSTALLED_APPS (docs/feature-authoring.md section 5), and this is that project.
The tests live here rather than inside each package for two reasons: the package
is what gets delivered and shipping a test suite to every customer store is
waste, and what actually needs checking is the Feature *installed* — its URLs
mounted, its templates found, its migrations applied — which only exists here.

Every module skips when its Feature is not installed, because this suite must
also pass on a base store with nothing on it. REQUIRE_FEATURE_TESTS=1 turns a
skip into a failure, which is what CI sets.

The advanced-promotions seam is tested in apps/promotions and apps/orders
instead, because what it exercises is base-store pricing deciding what to do
with a Feature's answer.
"""
