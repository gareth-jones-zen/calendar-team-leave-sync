"""team-leave: your reports' approved leave, copied from People First into your Outlook calendar.

    team-leave config                  create the settings file (if missing) and say where it is
    team-leave sign-in                 sign in to Microsoft, then to People First in a window
    team-leave sync --dry-run          what it would add, change, adopt and remove (writes nothing)
    team-leave sync                    bring your calendar into step with People First
    team-leave sync --adopt            ...taking over hand-made entries that match exactly
    team-leave sync --hook             ...then run the settings' after_run command, if anything changed
    team-leave sync --max-deletes N    allow more removals than max_deletes, this once
    team-leave status                  the last run, and the scheduled task
    team-leave autostart on            sync every every_minutes and at logon, and now (off, status)
"""

from __future__ import annotations

import argparse
import json
import sys

from m365_graph import SignInNeeded as GraphSignIn

import people_first

from . import __version__, config as cfg
from .run import client, load_state, logger, run


def main(argv: list | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="team-leave", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\team-leave\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    sub.add_parser("sign-in", help="sign in to Microsoft and People First (opens windows)")
    p = sub.add_parser("sync", help="bring the calendar into step with People First")
    p.add_argument("--dry-run", action="store_true", help="say what it would do; write nothing")
    p.add_argument("--adopt", action="store_true", help="take over hand-made entries that match exactly")
    p.add_argument("--hook", action="store_true", help="run the after_run command if anything changed")
    p.add_argument("--max-deletes", type=int, help="allow this many removals, this once")
    p.add_argument("--json", action="store_true", help="print the report as JSON")
    p.add_argument("--scheduled", action="store_true",
                   help="as the task runs it: only within hours, no browser, and notify problems")
    sub.add_parser("status", help="the last run, and the scheduled task")
    auto = sub.add_parser("autostart", help="sync every every_minutes and at logon (Task Scheduler)")
    auto.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    try:
        config = cfg.load(args.config)
        if args.command == "autostart":
            return _autostart(config, args.action)
        if args.command == "status":
            return _status(config)
        if args.command == "sign-in":
            client(config, interactive=True).token()
            print("Signed in to Microsoft. A window opens for People First: sign in there; it closes by itself.")
            people_first.sign_in_window(config.people_first)
            print("Signed in to People First.")
            return 0
        log = logger(config, echo=not args.json and not args.scheduled)
        try:
            report = run(config, dry_run=args.dry_run, adopt=args.adopt, hook=args.hook, scheduled=args.scheduled,
                         max_deletes=args.max_deletes, log=log)
        except Exception as error:
            if args.scheduled:
                return 1
            print(error, file=sys.stderr)
            return 2
        if report is None:
            return 0
        if args.json:
            print(json.dumps(report, indent=1))
        elif not args.scheduled:
            _print(report)
        return 0
    except (GraphSignIn, people_first.SignInNeeded, FileNotFoundError, ValueError, TimeoutError) as error:
        print(error, file=sys.stderr)
        return 2


def _print(report: dict) -> None:
    verb = "Would" if report["dry_run"] else "Did"
    sections = [("add", "added", None), ("change", "changed", "was"), ("adopt", "adopted", "was"),
                ("remove", "removed", None)]
    for word, key, was in sections:
        for item in report[key]:
            print(f"  {verb} {word}: {item['text']}" + (f"  (was: {item[was]})" if was else ""))
    for item in report["by_hand"]:
        hint = ("  (in pieces: never adopted; delete them for one entry)" if item["pieces"] > 1
                else "  (sync --adopt takes it over)")
        print(f"  Already in by hand, left alone: {item['entry']}{hint}")
    for item in report["unmatched_hand"]:
        print(f"  Hand-made, matches no approved leave in People First: {item['text']}")


def _status(config: cfg.Config) -> int:
    state = load_state(config)
    print(f"Last run:    {state.get('last_run', 'never')}")
    if state.get("last_result"):
        print(f"Result:      {state['last_result']}")
    if state.get("failures"):
        print(f"Failing:     {state['failures']} run(s) in a row; last: {state.get('last_error', '')}")
    return _autostart(config, "status")


def _autostart(config: cfg.Config, action: str) -> int:
    from . import autostart
    try:
        if action == "on":
            command = autostart.enable(config.path, config.every_minutes)
            print(f"Autostart is on: '{autostart.TASK}' runs every {config.every_minutes} minutes and at logon "
                  f"(syncing only {'-'.join(config.hours)}{', Mon-Fri' if config.weekdays else ''}), and has started "
                  f"now.\n  {command}\n  log: {config.log_file}")
        elif action == "off":
            print("Autostart is off." if autostart.disable() else "Autostart was already off.")
        else:
            info = autostart.status()
            if info is None:
                print("Autostart is off.")
            else:
                print(f"Autostart is on ('{autostart.TASK}').")
                for k in ("state", "last_run", "last_result", "next_run", "command"):
                    print(f"  {k.replace('_', ' '):12} {info.get(k, '')}")
        return 0
    except (RuntimeError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
