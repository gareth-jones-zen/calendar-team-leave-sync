"""Synthetic scenarios, run through the Python version (oracle.py), written as a golden file for the .NET tests.

    python tests/parity/scenarios.py 60 tests/TeamLeave.Tests/Golden/scenarios.json         # the committed set
    python tests/parity/scenarios.py 1500 %TEMP%/team-leave-golden.json --seed 7            # a bigger local run

Every person, name and date here is made up. Each case is a settings file and a few command lines run one after
another in one folder (so the state file carries over): leave shapes (half days, (AM)/(PM)/(from PM)/(to AM),
birthdays, pending and approved), entries the tool owns (current, stale, edited by hand, timed, past, duplicated,
orphaned), hand-made entries (exact, in pieces around a weekend, near misses), adoption, the safety stops, failures
of every kind, hours, the hook and the report.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import random
import shutil
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import oracle  # noqa: E402

from team_leave import calendar as cal, config as cfg, leave  # noqa: E402

FIRST = ["Jordan", "Robin", "Alex", "Sam", "Zoë", "İlker", "Anne-Marie", "J.", "Seán", "Ōta", "Li", "Mo", "a+b", "Daniel",
         "Mohammed", "Chloé", "Ægir", "Σοφία", "Max (Jo)", "Kelvin", "ſam", "Dan"]
LAST = ["Lee", "Hall", "O'Brien", "Smith", "Ng", "Đoković", "van der Berg", "Example", "Clark", "Sarwar", "Ölçer", "*Star*",
        "[x]", "Q$", "Ünal", "ΣΑΣ"]
TYPES = [("T1", "Holiday"), ("T2", "My Birthday"), ("T3", "Sickness"), ("T4", "Appointment"), (7, "Holiday Alt"), ("T5", "Training")]
PARTS = [("FULL", "All day"), ("HALF_AM", "AM"), ("HALF_PM", "PM"), ("X9", " pm "), ("X8", "Am"), (11, "ALL DAY")]


def iso(d: dt.date) -> str:
    return d.isoformat()


def settings(r: random.Random, hook: bool) -> tuple[str, dict]:
    """A settings file (TOML text), and what it says, for building matching entries."""
    lines = ['login_hint = "alex@example.com"', 'state_file = "state.json"', 'log_file = "log.txt"']
    if r.random() < 0.4:
        lines.append(f"max_deletes = {r.choice([0, 1, 2, 3, 5, 10])}")
    if r.random() < 0.3:
        lines.append(f"days_ahead = {r.choice([1, 7, 30, 90, 365, 730])}")
    if r.random() < 0.2:
        lines.append(f'hours = "{r.choice(["07:00-19:00", "00:00-23:59", "09:30-17:00", " 08:00 - 18:00 "])}"')
    if r.random() < 0.2:
        lines.append(f"weekdays = {r.choice(['true', 'false'])}")
    if hook:
        lines.append('after_run = ["python", "hook.py"]')
    lines += ["[people_first]", 'url = "https://example.people-first.com/"']
    if r.random() < 0.2:
        lines.append("depth = 2")
    if r.random() < 0.2:
        lines.append(f"states = {r.choice(['[\"Approved\"]', '[\"Approved\", \"AwaitingApproval\"]', '[\"AwaitingApproval\"]'])}")
    kinds = r.choice([None, ["Holiday"], ["Holiday", "My Birthday"], ["Holiday", "My Birthday", "Holiday Alt"],
                      ["Holiday", "Training"]])
    if kinds:
        lines.append("[types]")
        for k in kinds:
            lines.append(json.dumps(k) + " = " + json.dumps(r.choice(["{name} annual leave", "{name} annual leave",
                                                                       "{name} on leave", "Leave: {name}", "{name} {{off}}"])))
    if r.random() < 0.3:
        lines.append("[titles]")
        if r.random() < 0.5:
            lines.append('am = "{title} (morning)"')
        if r.random() < 0.5:
            lines.append('to_am = "{title} - back PM"')
    if r.random() < 0.5:
        lines.append("[event]")
        if r.random() < 0.5:
            lines.append(f"categories = {r.choice(['[\"Planned\"]', '[]', '[\"Leave\", \"Team\"]', '[\"Café\"]', '[\"Planned\", 3]'])}")
        if r.random() < 0.4:
            lines.append(f"reminder = {r.choice(['false', '0', '1080', '-5'])}")
        if r.random() < 0.3:
            lines.append(f'sensitivity = "{r.choice(["normal", "private", "personal", "confidential"])}"')
        if r.random() < 0.3:
            lines.append(f'show_as = "{r.choice(["free", "oof", "busy", "workingElsewhere", "tentative"])}"')
        if r.random() < 0.3:
            lines.append(f'time_zone = "{r.choice(["Europe/London", "GMT Standard Time", "UTC"])}"')
        if r.random() < 0.2:
            lines.append('body = "Synced: don’t edit ✓ (café)"')
    if r.random() < 0.4:
        lines.append("[names]")
        lines.append('"Daniel Clark" = "Dan Clark"')
        lines.append('"Mohammed Sarwar" = "Fahad Sarwar"')
    return "\n".join(lines) + "\n", {"kinds": kinds or ["Holiday"]}


def listing(events: list, r: random.Random) -> dict:
    types = [{"id": i, "value": v} for i, v in TYPES]
    parts = [{"id": i, "value": v} for i, v in PARTS]
    body = {"data": {"events": events}, "meta": {"events.eventTypeId": {"values": types},
                                                 "events.startTypeId": {"values": parts},
                                                 "events.endTypeId": {"values": parts}}}
    roll = r.random()
    if roll < 0.01:
        body = {"data": {"items": []}}
    elif roll < 0.02:
        body["meta"] = {}
    return body


def leave_events(r: random.Random, today: dt.date, n: int, pid: str) -> list:
    out = []
    for i in range(n):
        start = today + dt.timedelta(days=r.randint(-15, 400))
        length = r.choice([0, 0, 0, 1, 2, 4, 6, 7, 9, 13])
        end = start + dt.timedelta(days=length)
        sp = r.choice(["FULL", "FULL", "HALF_AM", "HALF_PM", "X9", "X8", 11, None, ""])
        ep = r.choice(["FULL", "FULL", "HALF_AM", "HALF_PM", "X9", None])
        e = {"eventId": r.choice([f"{pid}-A{i}", f"{pid}-A{i}", 1000 + i + int(pid[1:]) * 50]),
             "personId": pid, "startDate": iso(start) + r.choice(["", "T00:00:00", "T00:00:00.000Z"]),
             "endDate": iso(end) + r.choice(["", "T00:00:00"]),
             "eventTypeId": r.choice(["T1", "T1", "T1", "T2", "T3", "T4", 7, "T5", "T9"]),
             "state": r.choice(["Approved", "Approved", "Approved", "AwaitingApproval", "Rejected", "Cancelled"]),
             "startTypeId": sp, "endTypeId": ep, "absenceDurationUnit": "Daily"}
        roll = r.random()
        if roll < 0.01:
            del e["eventId"]
        elif roll < 0.02:
            e["startDate"] = "2026-13-40"
        elif roll < 0.025:
            e["startTypeId"] = "QQ"
        elif roll < 0.03:
            e["startDate"] = "20261224"
        out.append(e)
    return out


def item(eid: str, subject, start: dt.date, end: dt.date, all_day=True, absence=None, digest=None, **extra) -> dict:
    """A Graph event as calendarView or /me/events returns it (end inclusive here)."""
    if all_day:
        s, e = f"{iso(start)}T00:00:00.0000000", f"{iso(end + dt.timedelta(days=1))}T00:00:00.0000000"
    else:
        s, e = f"{iso(start)}T09:00:00.0000000", f"{iso(end)}T17:30:00.0000000"
    out = {"id": eid, "subject": subject, "isAllDay": all_day, "isOrganizer": extra.pop("organiser", True),
           "attendees": [{"emailAddress": {"address": "x@example.com"}}] * extra.pop("attendees", 0),
           "isCancelled": extra.pop("cancelled", False), "type": extra.pop("kind", "singleInstance"),
           "start": {"dateTime": s, "timeZone": "Europe/London"}, "end": {"dateTime": e, "timeZone": "Europe/London"}}
    props = []
    if absence is not None:
        props.append({"id": cal.ABSENCE_PROP if extra.pop("upper", False) is False else cal.ABSENCE_PROP.upper(), "value": absence})
    if digest is not None:
        props.append({"id": cal.HASH_PROP, "value": digest})
    if props:
        out["singleValueExtendedProperties"] = props
    if extra.pop("no_organiser", False):
        del out["isOrganizer"]
    return out


def calendar(r: random.Random, config, absences: list, people: list, today: dt.date) -> list:
    items, n = [], 0

    def nid():
        nonlocal n
        n += 1
        return f"E{n}"

    for a in absences:
        roll = r.random()
        payload = cal.body(config, a)
        good = cal.digest(payload)
        if roll < 0.45:
            items.append(item(nid(), payload["subject"], a.start, a.end, absence=a.id, digest=good, upper=r.random() < 0.1))
        elif roll < 0.5:
            items.append(item(nid(), payload["subject"], a.start, a.end, absence=a.id, digest="0000000000000000"))
        elif roll < 0.55:
            items.append(item(nid(), "edited by hand", a.start, a.end, absence=a.id, digest=good))
        elif roll < 0.58:
            items.append(item(nid(), payload["subject"], a.start, a.end, all_day=False, absence=a.id, digest=good))
        elif roll < 0.62:
            items.append(item(nid(), payload["subject"], a.start, a.end, absence=a.id, digest=good))
            items.append(item(nid(), payload["subject"], a.start, a.end, absence=a.id, digest=good))
        elif roll < 0.75:
            name = a.person.name
            subject = r.choice([f"{name} annual leave", f"{name.lower()} on leave", f"{name} annual leave (AM)",
                                f" {name}  annual leave PM ", f"{name} annual leave\n", f"{name} on leave (from PM) (to AM)",
                                f"{name} annual leave - skiing", f"{name}s annual leave", f"Re: {name} annual leave"])
            kind = r.random()
            if kind < 0.6:
                items.append(item(nid(), subject, a.start, a.end, organiser=r.random() > 0.1,
                                  attendees=1 if r.random() < 0.1 else 0, all_day=r.random() > 0.05,
                                  cancelled=r.random() < 0.05))
            else:
                # in pieces: split at a random point, maybe leaving a gap
                if a.end > a.start:
                    cut = a.start + dt.timedelta(days=r.randint(0, (a.end - a.start).days))
                    gap = r.choice([0, 0, 1, 2, 3])
                    items.append(item(nid(), subject, a.start, cut))
                    if cut + dt.timedelta(days=1 + gap) <= a.end:
                        items.append(item(nid(), subject, cut + dt.timedelta(days=1 + gap), a.end))
                else:
                    items.append(item(nid(), subject, a.start, a.end))
    # orphans, the past, and beyond the window
    for _ in range(r.choice([0, 0, 0, 0, 1, 1, 2, 4, 7])):
        start = today + dt.timedelta(days=r.randint(-30, 800))
        end = start + dt.timedelta(days=r.choice([0, 1, 3]))
        items.append(item(nid(), r.choice(["Someone annual leave", "Old leave", None, ""]), start, end,
                          absence=r.choice([f"GONE{r.randint(1, 99)}", ""]), digest="1234"))
    # other things in the calendar
    for _ in range(r.randint(0, 5)):
        start = today + dt.timedelta(days=r.randint(-5, 100))
        p = r.choice(people) if people else None
        subject = r.choice(["Team offsite", "1:1", f"{p.name} on leave" if p else "x", f"{p.name} annual leave (to AM)" if p else "y"])
        items.append(item(nid(), subject, start, start + dt.timedelta(days=r.choice([0, 1, 4])), all_day=r.random() > 0.3,
                          no_organiser=r.random() < 0.1))
    r.shuffle(items)
    return items


def people_tables(r: random.Random, depth2: bool) -> tuple[dict, list]:
    """People First's team listings (by manager id) and every person, as dicts."""
    team, everyone = {}, []
    count = r.choice([0, 1, 2, 2, 3, 3, 4, 4, 6, 6])
    names = set()
    for i in range(count):
        first, last = r.choice(FIRST), r.choice(LAST)
        if r.random() < 0.1:
            first, last = "Daniel", "Clark"
        member = {"personId": r.choice([f"P{i + 1}", f"P{i + 1}", i + 1]), "firstName": r.choice([first, f" {first} ", first]),
                  "lastName": last, "knownAs": first, "jobTitle": "Solutions Architect"}
        if r.random() < 0.05:
            member["lastName"] = None
        names.add((first, last))
        everyone.append(member)
    team["ME"] = everyone
    if depth2 and everyone:
        sub = [{"personId": "P90", "firstName": "Sub", "lastName": "Report"}, everyone[0]]
        team[str(everyone[0]["personId"])] = sub
    return team, everyone


def case(r: random.Random, index: int) -> dict:
    hook = r.random() < 0.5
    toml, _ = settings(r, hook)
    folder = pathlib.Path(tempfile.mkdtemp(prefix="team-leave-oracle-")).resolve()
    try:
        path = folder / "config.toml"
        path.write_text(toml, encoding="utf-8")
        try:
            config = cfg.load(path)
        except ValueError:
            return {"settings": toml, "steps": []}
        today = dt.date(2026, 11, 2) + dt.timedelta(days=r.randint(0, 400))
        team, everyone = people_tables(r, config.depth > 1)
        steps = []
        for _ in range(r.choice([1, 1, 2, 3, 5])):
            now = dt.datetime.combine(today, dt.time(r.choice([6, 7, 10, 12, 18, 19, 23]), r.choice([0, 1, 59])))
            events = {}
            for member in team.get("ME", []) + team.get(str(everyone[0]["personId"]), [])[:1] if everyone else []:
                pid = str(member["personId"])
                events[pid] = listing(leave_events(r, today, r.choice([0, 1, 2, 3, 5]), pid if pid.startswith("P") else f"P{pid}"), r)
            pf = {"me": "ME", "team": team, "events": events}
            roll = r.random()
            if roll < 0.04:
                pf["session"] = r.choice(["sign-in", "timeout", "error"])
            elif roll < 0.08 and everyone:
                pf["fail"] = {"at": f"events {everyone[-1]['personId']} {today} {today + dt.timedelta(days=config.days_ahead)}",
                              "kind": r.choice(["error", "sign-in"]), "message": "People First answered 503 to /hrm/people/P/events"}
            # what Python would read, to build a calendar that matches it
            absences, people = [], []
            try:
                calls = []
                people, absences = leave.read(config, oracle.FakeApi({k: v for k, v in pf.items() if k != "fail"}, calls), today,
                                              log=lambda _: None)
            except Exception:   # noqa: BLE001: the run will fail the same way
                pass
            items = calendar(r, config, absences, people, today)
            if r.random() < 0.1 and absences:
                # leave cancelled in People First: the entries stay, the absences go
                for e in pf["events"].values():
                    if isinstance(e.get("data"), dict) and "events" in e["data"]:
                        e["data"]["events"] = []
            argv = r.choice([["sync"], ["sync"], ["sync", "--dry-run"], ["sync", "--hook"], ["sync", "--hook"],
                             ["sync", "--adopt"], ["sync", "--adopt", "--dry-run"], ["sync", "--json"], ["sync", "--dry-run", "--json"],
                             ["sync", "--hook", "--scheduled"], ["sync", "--hook", "--scheduled"], ["sync", "--scheduled", "--json"],
                             ["sync", "--max-deletes", str(r.choice([0, 1, 3, 50]))], ["status"], ["sync", "--adopt", "--hook"],
                             ["sync", "--max-deletes=2", "--hook"], ["sync", "--hook"], ["sync", "--adopt", "--json"], ["sync", "--hook", "--dry-run"]]
                            + ([["sync", "--bogus"], ["autostart", "x"], ["sync", "--max-deletes", "x"]] if r.random() < 0.1 else []))
            s = {"argv": argv, "now": now.isoformat(), "pf": pf, "calendar": items}
            roll = r.random()
            if roll < 0.04:
                s["graph_sign_in"] = "No Microsoft sign-in for alex@example.com. At a console, run: team-leave sign-in"
            elif roll < 0.1:
                s["fail_write"] = r.randint(1, 3)
                s["fail_status"], s["fail_reason"] = r.choice([(503, "Service Unavailable"), (404, "Not Found"), (409, "Conflict")])
            if hook:
                hr = r.random()
                if hr < 0.2:
                    s["hook_exit"] = 3
                    s["hook_stdout"] = "queued 2 items\r\n\n  done  \n"
                    s["hook_stderr"] = "warning: " + "x" * 250
                elif hr < 0.25:
                    s["hook_missing"] = True
            steps.append({**s, "expected": oracle.step(path, s)})
            today += dt.timedelta(days=r.choice([0, 0, 1, 3]))
        return {"settings": toml, "steps": steps}
    finally:
        shutil.rmtree(folder, ignore_errors=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("count", type=int)
    parser.add_argument("output")
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()
    r = random.Random(args.seed)
    cases = [case(r, i) for i in range(args.count)]
    steps = sum(len(c["steps"]) for c in cases)
    crashes = sum(1 for c in cases for s in c["steps"] if "crash" in s["expected"])
    pathlib.Path(args.output).write_text(json.dumps({"seed": args.seed, "cases": cases}, ensure_ascii=False),
                                         encoding="utf-8")
    print(f"{len(cases)} cases, {steps} steps, {crashes} Python crashes -> {args.output}")


if __name__ == "__main__":
    main()
