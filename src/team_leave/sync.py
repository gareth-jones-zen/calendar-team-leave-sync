"""One reconcile: People First's leave against the entries team-leave owns in your calendar.

For each kept absence (sync.plan):
- **no owned entry:** create one, **unless you already made one by hand**: an entry you organise, with no
  attendees, all-day, titled "<Name> annual leave" or "<Name> on leave" (half-day marks allowed), on exactly
  the same first and last day, **or several such entries that between them cover every weekday of it** (leave
  entered in pieces around a weekend). That suppresses the create, so nothing is duplicated. **With --adopt it's
  taken over instead**: stamped as team-leave's and rewritten to the configured title and settings. Only a
  single exact match starting today or later is adopted; an ongoing one, or one in pieces, is left as it is;
- **an owned entry that differs** (the absence changed, the settings changed, or its title or dates were
  edited by hand): rewritten;
- **an owned entry with no absence** (cancelled, rejected, or the person left your team): deleted.

**What it never touches:** entries without its property (unless adopted), and **owned entries that
ended before today**: the past stays as it was. Owned entries starting after the window are left too.

**Safety stops, which write nothing at all:**
- every People First read must succeed first (leave.read raises otherwise), so a failed read can't look
  like cancelled leave;
- **"everything vanished":** People First gives no leave at all while team-leave owns future entries;
- **more than max_deletes removals** in one run.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass, field

from . import calendar as cal
from .config import Config
from .leave import Absence, Person


class SafetyStop(RuntimeError):
    """The plan looked wrong, so nothing was written."""


@dataclass
class Plan:
    create: list = field(default_factory=list)      # [(absence, payload)]
    update: list = field(default_factory=list)      # [(absence, payload, entry)]
    adopt: list = field(default_factory=list)       # [(absence, payload, entry)]
    delete: list = field(default_factory=list)      # [entry]
    by_hand: list = field(default_factory=list)     # [(absence, [entries])]: already entered by hand, left alone
    unmatched_hand: list = field(default_factory=list)   # [entry]: looks like a report's leave, matches no absence
    unchanged: int = 0

    @property
    def writes(self) -> int:
        return len(self.create) + len(self.update) + len(self.adopt) + len(self.delete)


def _fmt(first: dt.date, last: dt.date) -> str:
    return f"{first:%d/%m}" if first == last else f"{first:%d/%m} to {last:%d/%m}"


def _pieces(absence: Absence, candidates: list) -> list:
    """Hand-made entries that between them cover every weekday of the absence, each lying wholly inside it:
    leave entered by hand in pieces, such as Wed-Fri and Mon-Wed around a weekend. [] if they don't."""
    inside = [e for e in candidates if absence.start <= e.start and e.end <= absence.end]
    if not inside:
        return []
    day = absence.start
    while day <= absence.end:
        if day.weekday() < 5 and not any(e.start <= day <= e.end for e in inside):
            return []
        day += dt.timedelta(days=1)
    return sorted(inside, key=lambda e: e.start)


def plan(config: Config, people: list[Person], absences: list[Absence], calendar, today: dt.date,
         adopt: bool = False, max_deletes: int | None = None, survey: bool = False) -> Plan:
    """survey: read the whole window for hand-made entries, to report ones that match no leave (a dry run
    does). Otherwise hand-made entries are read only across the span of absences still to create."""
    window_end = today + dt.timedelta(days=config.days_ahead)
    out = Plan()
    owned: dict = {}
    for item in calendar.owned():
        if item.end < today or item.start > window_end:
            continue   # the past, and anything beyond the window, are never touched
        if item.absence in owned:
            out.delete.append(item)   # a second entry for one absence (a run cut short mid-write): tidy it
        else:
            owned[item.absence] = item
    wanted = {a.id: a for a in absences}

    pending = []
    for absence in absences:
        payload = cal.body(config, absence)
        current = owned.get(absence.id)
        if current is None:
            pending.append((absence, payload))
        elif cal.differs(current, payload):
            out.update.append((absence, payload, current))
        else:
            out.unchanged += 1
    out.delete += [item for aid, item in owned.items() if aid not in wanted]

    # Hand-made entries: only read when there's something to create, or to report on a dry run.
    if pending or survey:
        first = min([a.start for a, _ in pending] + [today])
        last = window_end if survey else max(a.end for a, _ in pending)
        hand = [e for e in calendar.between(first, last) if not e.absence
                and e.organiser and not e.attendees and not e.cancelled and e.all_day]
        patterns = {p.name: cal.hand_pattern(p.name) for p in people}
        used = set()
        for absence, payload in pending:
            pattern = patterns.get(absence.person.name) or cal.hand_pattern(absence.person.name)
            match = next((e for e in hand if e.id not in used and pattern.match(e.subject)
                          and e.start == absence.start and e.end == absence.end), None)
            if match is None:
                pieces = _pieces(absence, [e for e in hand if e.id not in used and pattern.match(e.subject)])
                if pieces:   # split by hand (around a weekend, say): left alone, never adopted
                    used.update(e.id for e in pieces)
                    out.by_hand.append((absence, pieces))
                else:
                    out.create.append((absence, payload))
                continue
            used.add(match.id)
            if adopt and match.start >= today:
                out.adopt.append((absence, payload, match))
            else:
                out.by_hand.append((absence, [match]))
        out.unmatched_hand = [e for e in hand if e.id not in used and e.end >= today
                              and any(p.match(e.subject) for p in patterns.values())]

    # Safety stops: nothing is written if either trips.
    cap = config.max_deletes if max_deletes is None else max_deletes
    if not absences and owned and (max_deletes is None or max_deletes < len(owned)):
        raise SafetyStop(f"People First gave no leave at all, but team-leave has {len(owned)} future entries. "
                         "Nothing was changed. If everyone's leave really was cancelled, run sync by hand with "
                         f"--max-deletes {len(owned)}.")
    if len(out.delete) > cap:
        raise SafetyStop(f"{len(out.delete)} entries would be removed, more than max_deletes ({cap}). Nothing was "
                         f"changed. Check with sync --dry-run; if it's right, run sync with --max-deletes {len(out.delete)}.")
    return out


def apply(plan_: Plan, calendar, log=print) -> None:
    """Write the plan: creates, then updates and adoptions, then deletes. Each write is logged by id."""
    for absence, payload in plan_.create:
        calendar.create(cal.stamped(payload, absence.id))
        log(f"created entry for absence {absence.id}")
    for verb, items in (("updated", plan_.update), ("adopted", plan_.adopt)):
        for absence, payload, current in items:
            calendar.update(current.id, cal.stamped(payload, absence.id))
            log(f"{verb} entry for absence {absence.id}")
    for item in plan_.delete:
        calendar.delete(item.id)
        log(f"deleted entry for absence {item.absence}")


def report(config: Config, plan_: Plan, dry_run: bool) -> dict:
    """What changed, for the console and the after-run hook: names and dates, never the type."""
    def line(absence: Absence) -> dict:
        title = absence.title(config)
        return {"name": absence.person.name, "title": title, "start": absence.start.isoformat(),
                "end": absence.end.isoformat(), "text": f"{title} {_fmt(absence.start, absence.end)}"}

    def gone(item: cal.Entry) -> dict:
        return {"title": item.subject, "start": item.start.isoformat(), "end": item.end.isoformat(),
                "text": f"{item.subject} {_fmt(item.start, item.end)}"}

    out = {"config": str(config.path), "dry_run": dry_run,
           "added": [line(a) for a, _ in plan_.create],
           "changed": [{**line(a), "was": f"{e.subject} {_fmt(e.start, e.end)}"} for a, _, e in plan_.update],
           "adopted": [{**line(a), "was": f"{e.subject} {_fmt(e.start, e.end)}"} for a, _, e in plan_.adopt],
           "removed": [gone(e) for e in plan_.delete],
           "by_hand": [{**line(a), "entry": " + ".join(f"{e.subject} {_fmt(e.start, e.end)}" for e in es),
                        "pieces": len(es)} for a, es in plan_.by_hand],
           "unmatched_hand": [gone(e) for e in plan_.unmatched_hand],
           "unchanged": plan_.unchanged}
    out["summary"] = summary(out)
    return out


def summary(report_: dict) -> str:
    text = (f"{len(report_['added'])} added, {len(report_['changed'])} changed, {len(report_['removed'])} removed, "
            f"{report_['unchanged']} unchanged")
    if report_["adopted"]:
        text += f", {len(report_['adopted'])} adopted"
    if report_["by_hand"]:
        text += f", {len(report_['by_hand'])} already in by hand"
    return text
