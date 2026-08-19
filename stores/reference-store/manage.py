#!/usr/bin/env python
"""Django's command-line utility for the KNIGHT reference store."""

import os
import sys


def main() -> None:
    os.environ.setdefault("DJANGO_SETTINGS_MODULE", "config.settings")

    try:
        from django.core.management import execute_from_command_line
    except ImportError as exc:  # pragma: no cover - only hit on a broken install
        raise ImportError(
            "Django is not importable. Activate the virtual environment and run "
            "`pip install -r requirements.txt`."
        ) from exc

    execute_from_command_line(sys.argv)


if __name__ == "__main__":
    main()
