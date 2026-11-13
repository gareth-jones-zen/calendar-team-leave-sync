"""Live, read-only parity checks on GJ's laptop. Never run by CI. Every input is a copy, and nothing is written but
files in the folder given (which holds the team's leave: never print it, never commit it, delete it afterwards).
It prints counts and hashes only.

    python tests/parity/live.py record-pf <settings.toml> <out.json>            People First (GETs), as team-leave reads it
    python tests/parity/live.py record-graph <settings.toml> <yyyy-mm-dd> <out.json>   owned() and a wide window (GETs)
    python tests/parity/live.py compare <a.json> <b.json>                       equal? by canonical hash, per section
    python tests/parity/live.py build <pf.json> <graph.json> <settings.toml> <yyyy-mm-dd> <out golden>
    python tests/parity/live.py report <report.json> <config path>             a dry run's report: counts and hash

`build` runs the Python version through oracle.py on the recorded inputs (a dry run, a sync whose writes go to the
fake Graph, and a dry run with --adopt), for ParityRunner's replay.
"""

from __future__ import annotations

import datetime as dt
import hashlib
import json
import pathlib
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import oracle  # noqa: E402
import people_first  # noqa: E402

from team_leave import calendar as cal, config as cfg, leave, run as run_mod  # noqa: E402


def sha(value) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()[:16]


class Recording:
    def __init__(self, api, out):
        self.api, self.out = api, out

    def me(self):
        self.out["calls"].append("me")
        self.out["me"] = self.api.me()
        return self.out["me"]

    def team_members(self, person):
        self.out["calls"].append(f"team {person}")
        members = self.api.team_members(person)
        self.out["team"][person] = members
        return members

    def events(self, person, start, end):
        self.out["calls"].append(f"events {person} {start} {end}")
        body = self.api.events(person, start, end)
        self.out["events"][person] = body
        return body


def record_pf(settings: str, output: str) -> None:
    config = cfg.load(settings)
    out = {"me": None, "team": {}, "events": {}, "calls": []}
    with people_first.session(config.people_first, log=lambda _: None, lock_wait=run_mod.LOCK_WAIT) as api:
        leave.read(config, Recording(api, out), dt.date.today(), log=lambda _: None)
    pathlib.Path(output).write_text(json.dumps(out, sort_keys=True), encoding="utf-8")
    print(f"people first: {len(out['calls'])} calls, {len(out['team'])} team listings, {len(out['events'])} event listings; sha {sha(out)}")


def record_graph(settings: str, today: str, output: str) -> None:
    config = cfg.load(settings)
    graph = run_mod.client(config, interactive=False)
    calendar = cal.Calendar(graph, config)
    day = dt.date.fromisoformat(today)
    owned_url = (f"/me/events?$filter={cal.urllib.parse.quote(f'singleValueExtendedProperties/Any(ep: ep/id eq {chr(39)}{cal.ABSENCE_PROP}{chr(39)} and ep/value ne null)')}"
                 f"&$expand={cal.urllib.parse.quote(f'singleValueExtendedProperties($filter=id eq {chr(39)}{cal.ABSENCE_PROP}{chr(39)} or id eq {chr(39)}{cal.HASH_PROP}{chr(39)})')}"
                 f"&$select={cal.SELECT}&$top=100")
    first, last = day - dt.timedelta(days=60), day + dt.timedelta(days=config.days_ahead)
    between_url = (f"/me/calendarView?startDateTime={first.isoformat()}T00:00:00"
                   f"&endDateTime={(last + dt.timedelta(days=1)).isoformat()}T00:00:00"
                   f"&$expand={cal.urllib.parse.quote(f'singleValueExtendedProperties($filter=id eq {chr(39)}{cal.ABSENCE_PROP}{chr(39)})')}"
                   f"&$select={cal.SELECT}&$top=500")
    owned = list(graph.pages(owned_url, scopes=cal.SCOPES, headers=calendar.headers))
    between = list(graph.pages(between_url, scopes=cal.SCOPES, headers=calendar.headers))
    out = {"owned": owned, "between": between}
    pathlib.Path(output).write_text(json.dumps(out, sort_keys=True), encoding="utf-8")
    print(f"graph: {len(owned)} owned, {len(between)} in the window; owned sha {sha(owned)}, window sha {sha(between)}")


def compare(a: str, b: str) -> None:
    x, y = (json.loads(pathlib.Path(p).read_text(encoding="utf-8")) for p in (a, b))
    for key in sorted(set(x) | set(y)):
        same = sha(x.get(key)) == sha(y.get(key))
        size = len(x.get(key) or []) if isinstance(x.get(key), (list, dict)) else 1
        print(f"  {key}: {size} item(s), {'identical' if same else 'DIFFERENT'} ({sha(x.get(key))} / {sha(y.get(key))})")


def build(pf: str, graph: str, settings: str, today: str, output: str) -> None:
    pf_data = json.loads(pathlib.Path(pf).read_text(encoding="utf-8"))
    graph_data = json.loads(pathlib.Path(graph).read_text(encoding="utf-8"))
    by_id = {i["id"]: i for i in graph_data["between"]}
    by_id.update({i["id"]: i for i in graph_data["owned"]})   # the owned listing carries the hash property too
    text = pathlib.Path(settings).read_text(encoding="utf-8")
    lines = [line for line in text.splitlines()
             if not line.split("=")[0].strip() in ("state_file", "log_file", "after_run", "msal_cache")]
    text = 'state_file = "state.json"\nlog_file = "log.txt"\n' + "\n".join(lines) + "\n"
    folder = pathlib.Path(tempfile.mkdtemp(prefix="oracle-", dir=pathlib.Path(output).parent)).resolve()
    try:
        path = folder / "config.toml"
        path.write_text(text, encoding="utf-8")
        steps = []
        for argv in (["sync", "--dry-run", "--json"], ["sync", "--json"], ["sync", "--adopt", "--dry-run", "--json"]):
            s = {"argv": argv, "now": f"{today}T10:00:00",
                 "pf": {"me": pf_data["me"], "team": pf_data["team"], "events": pf_data["events"]},
                 "calendar": list(by_id.values())}
            steps.append({**s, "expected": oracle.step(path, s)})
            (folder / "state.json").unlink(missing_ok=True)   # each step from a clean state
            (folder / "log.txt").unlink(missing_ok=True)
        golden = {"cases": [{"settings": text, "steps": steps}]}
        pathlib.Path(output).write_text(json.dumps(golden, ensure_ascii=False), encoding="utf-8")
        for st in steps:
            e = st["expected"]
            print(f"  {' '.join(st['argv'])}: exit {e.get('exit')}, {len(e['reads'])} reads, {len(e['writes'])} writes, "
                  f"{len(e['pf_calls'])} People First calls, writes sha {sha(e['writes'])}")
    finally:
        import shutil
        shutil.rmtree(folder, ignore_errors=True)


def report(path: str, config_path: str) -> None:
    raw = pathlib.Path(path).read_bytes().decode("utf-8").replace("\r\n", "\n")
    data = json.loads(raw)
    data["config"] = "{CONFIG}"
    counts = {k: len(v) for k, v in data.items() if isinstance(v, list)}
    print(f"  report: {counts}, unchanged {data.get('unchanged')}, sha {sha(data)}")


if __name__ == "__main__":
    command, *rest = sys.argv[1:]
    {"record-pf": record_pf, "record-graph": record_graph, "compare": compare, "build": build, "report": report}[command](*rest)
