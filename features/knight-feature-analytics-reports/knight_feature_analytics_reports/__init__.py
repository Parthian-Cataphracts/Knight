"""
Analytics Reports: a reporting surface over the analytics event stream.

Owns no tables. It reads what `knight-feature-analytics-core` records, through
that feature's published service functions rather than its models — which is why
the manifest can depend on a version *range* instead of pinning an exact release.
"""
