"""
Analytics Core: the event table every other analytics feature builds on.

A Feature is a normal Django app that happens to be delivered rather than
checked in. It owns its own models and migrations under its own app label, and
integrates with the store only through documented extension points — never by
editing store code (docs/feature-delivery.md section 4).
"""
