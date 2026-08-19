"""
The agent's command line.

Two commands and no more: enrol once, then run. Anything an operator might want
to do beyond that — revoking, re-provisioning — is done in KNIGHT, where it is
audited, rather than on the box, where it is not.
"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

from . import __version__
from .agent import DEFAULT_STATE_PATH, AgentError, KnightAgent


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="knight-agent", description="The KNIGHT agent.")
    parser.add_argument("--base-url", default="http://localhost:5008", help="KNIGHT's address.")
    parser.add_argument("--state", type=Path, default=DEFAULT_STATE_PATH, help="Where the agent keeps its credential.")
    parser.add_argument("--store", type=Path, action="append", default=[], help="A store directory this agent manages. Repeatable.")
    parser.add_argument("--verbose", action="store_true")

    subparsers = parser.add_subparsers(dest="command", required=True)

    enrol = subparsers.add_parser("enrol", help="Exchange a one-time provisioning token for a credential.")
    enrol.add_argument("--token", required=True)

    run = subparsers.add_parser("run", help="Heartbeat and apply queued jobs until stopped.")
    run.add_argument("--once", action="store_true", help="Do one pass and exit. Useful from cron and in tests.")

    subparsers.add_parser("version", help="Print the agent version.")

    args = parser.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s %(message)s",
    )

    if args.command == "version":
        print(__version__)
        return 0

    agent = KnightAgent(args.base_url, args.state, [Path(store) for store in args.store])

    try:
        if args.command == "enrol":
            state = agent.enrol(args.token, __version__)
            print(f"Enrolled as agent {state.agent_id} on server {state.server_id}.")
            return 0

        agent.run(once=args.once)
        return 0
    except AgentError as exc:
        # A clean message and a non-zero exit. An operator running this by hand
        # should not have to read a stack trace to learn the token was refused.
        print(f"knight-agent: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
