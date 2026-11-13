"""The Python version as an oracle: runs team-leave's own command line on given inputs, with the clock frozen and
People First, Graph, notifications, the hook and Task Scheduler faked, and records everything it did.

Used by scenarios.py (synthetic, committed goldens) and live.py (recorded real inputs, local only). It imports the
Python package from this repository's src/ (the same code as the v0.1.0 tag), never the installed tool, and nothing it
runs can reach People First, Graph or the real state file: every way out is replaced first.
"""

from __future__ import annotations

import contextlib
import copy
import datetime as real_dt
import io
import json
import pathlib
import subprocess
import sys
import types
import urllib.error
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

import m365_graph  # noqa: E402
import people_first  # noqa: E402
import win_background.log as wb_log  # noqa: E402

from team_leave import autostart, calendar as cal, cli, leave, run as run_mod  # noqa: E402

assert pathlib.Path(run_mod.__file__).resolve().is_relative_to(ROOT / "src"), "imported the installed tool, not this checkout"

PLACEHOLDER = "{DIR}"


def frozen(now: real_dt.datetime):
    """A stand-in for a module's `dt` whose now() and today() are fixed."""
    class FrozenDatetime(real_dt.datetime):
        @classmethod
        def now(cls, tz=None):
            return cls(now.year, now.month, now.day, now.hour, now.minute, now.second, now.microsecond)

    class FrozenDate(real_dt.date):
        @classmethod
        def today(cls):
            return cls(now.year, now.month, now.day)

    return types.SimpleNamespace(datetime=FrozenDatetime, date=FrozenDate, timedelta=real_dt.timedelta)


def placeholder(text: str, folder: pathlib.Path) -> str:
    for raw in {str(folder.resolve()), str(folder)}:
        text = text.replace(json.dumps(raw)[1:-1], PLACEHOLDER).replace(raw, PLACEHOLDER)
    return text


class FakeApi:
    """People First answering from the step's tables, recording each call as a key."""

    def __init__(self, pf: dict, calls: list):
        self.pf, self.calls = pf, calls

    def _call(self, key: str):
        self.calls.append(key)
        fail = self.pf.get("fail")
        if fail and fail["at"] == key:
            if fail["kind"] == "sign-in":
                raise people_first.SignInNeeded(fail["message"])
            raise people_first.PeopleFirstError(fail["message"])

    def me(self):
        self._call("me")
        return self.pf.get("me", "ME")

    def team_members(self, person):
        self._call(f"team {person}")
        return copy.deepcopy(self.pf.get("team", {}).get(person, []))

    def events(self, person, start, end):
        self._call(f"events {person} {start} {end}")
        return copy.deepcopy(self.pf.get("events", {}).get(person, {"data": {"events": []}, "meta": {}}))


def overlaps(item: dict, start: str, end: str) -> bool:
    s, e = item["start"]["dateTime"][:19], item["end"]["dateTime"][:19]
    return s < end and (e > start or (s == e and s >= start))


class FakeGraph:
    """The calendar as a list of Graph items. Reads are answered as Graph would and recorded with what they returned;
    writes are recorded exactly as m365_graph would send them (json.dumps(body))."""

    def __init__(self, step: dict, reads: list, writes: list):
        self.items, self.step, self.reads, self.writes = step.get("calendar", []), step, reads, writes

    def pages(self, url, scopes=None, headers=None):
        if self.step.get("graph_sign_in"):
            raise m365_graph.SignInNeeded(self.step["graph_sign_in"])
        if url.startswith("/me/events?$filter="):
            found = [i for i in self.items if any(
                (p.get("id") or "").lower() == cal.ABSENCE_PROP.lower() and p.get("value") is not None
                for p in i.get("singleValueExtendedProperties") or [])]
        elif url.startswith("/me/calendarView?"):
            query = urllib.parse.parse_qs(url.split("?", 1)[1])
            start, end = query["startDateTime"][0], query["endDateTime"][0]
            found = [i for i in self.items if overlaps(i, start, end)]
            found = [{**i, "singleValueExtendedProperties": [p for p in i.get("singleValueExtendedProperties") or []
                                                             if (p.get("id") or "").lower() == cal.ABSENCE_PROP.lower()]}
                     for i in found]
        else:
            raise AssertionError(f"unexpected read {url}")
        found = copy.deepcopy(found)
        self.reads.append({"url": url, "scopes": list(scopes or []), "headers": dict(headers or {}), "items": found})
        yield from copy.deepcopy(found)

    def request(self, url, method="GET", body=None, scopes=None, headers=None, interactive=None):
        assert method != "GET", "reads go through pages()"
        self.writes.append({"method": method, "url": url, "body": None if body is None else json.dumps(body),
                            "scopes": list(scopes or []), "headers": dict(headers or {})})
        if self.step.get("fail_write") == len(self.writes):
            raise urllib.error.HTTPError("https://graph.microsoft.com/v1.0" + url, self.step.get("fail_status", 503),
                                         self.step.get("fail_reason", "Service Unavailable"), {}, None)
        return {"id": f"new{len(self.writes)}"} if method == "POST" else {}

    def token(self, *args, **kwargs):
        return "token"


def step(config_path: pathlib.Path, s: dict) -> dict:
    """One command line, as cli.main runs it, with everything it produced."""
    folder = config_path.parent
    now = real_dt.datetime.fromisoformat(s["now"])
    pf_calls, reads, writes, alerts, hooks = [], [], [], [], []
    graph = FakeGraph(s, reads, writes)

    def read_people_first(config, today, log, headless=True):
        pf = s.get("pf", {})
        session = pf.get("session")
        if session == "sign-in":
            raise people_first.SignInNeeded(pf.get("session_message", "People First's sign-in wants a password."))
        if session == "timeout":
            raise TimeoutError(pf.get("session_message", "Another tool kept the profile."))
        if session == "error":
            raise people_first.PeopleFirstError(pf.get("session_message", "Couldn't start Edge for People First"))
        return leave.read(config, FakeApi(pf, pf_calls), today, log=log)

    def fake_run(args, input=None, **kwargs):
        hooks.append(input)
        if s.get("hook_missing"):
            raise FileNotFoundError(None, "The system cannot find the file specified", None, 2)   # as Windows raises it
        return subprocess.CompletedProcess(args, s.get("hook_exit", 0), s.get("hook_stdout", ""), s.get("hook_stderr", ""))

    saved = (run_mod.dt, wb_log.dt, run_mod.subprocess.run, run_mod.client, run_mod.read_people_first,
             run_mod.run.__defaults__, autostart.status)
    defaults = list(run_mod.run.__defaults__)
    defaults[-1] = lambda title, lines: alerts.append({"title": title, "lines": [str(x) for x in lines]})
    run_mod.dt = wb_log.dt = frozen(now)
    run_mod.subprocess.run = fake_run
    run_mod.client = lambda config, interactive=None: graph
    run_mod.read_people_first = read_people_first
    run_mod.run.__defaults__ = tuple(defaults)
    autostart.status = lambda: None
    out, err = io.StringIO(), io.StringIO()
    outcome = {}
    try:
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            try:
                outcome["exit"] = cli.main(["--config", str(config_path), *s["argv"]])
            except SystemExit as exit_:   # argparse
                outcome["exit"] = exit_.code
            except Exception as error:   # noqa: BLE001: Python crashed; recorded, compared by kind only
                outcome["crash"] = type(error).__name__
    finally:
        (run_mod.dt, wb_log.dt, run_mod.subprocess.run, run_mod.client, run_mod.read_people_first,
         run_mod.run.__defaults__, autostart.status) = saved
    state = folder / "state.json"
    log = folder / "log.txt"
    outcome.update({
        "stdout": placeholder(out.getvalue(), folder),
        "stderr": placeholder(err.getvalue(), folder),
        "state": state.read_text(encoding="utf-8") if state.exists() else None,
        "log": placeholder(log.read_bytes().decode("utf-8"), folder) if log.exists() else None,
        "pf_calls": pf_calls,
        "reads": reads,
        "writes": writes,
        "alerts": [{"title": a["title"], "lines": [placeholder(x, folder) for x in a["lines"]]} for a in alerts],
        "hooks": [placeholder(h, folder) for h in hooks],
    })
    return outcome
