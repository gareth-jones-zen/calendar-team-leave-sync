"""Leave from People First: who's off, when, and what the calendar entry is called.

**Read only what's needed, and keep less** (data minimisation):
- the only listing read is `/hrm/people/{id}/events` (with `annotate=t`, which names the types). It has
  no reason or notes field. `/hrm/absences/{id}`, which has the reason, and the Calendar API, which has
  free text, are never read;
- **only the configured types and states are kept**, as each listing is read. Everything else (sickness,
  appointments, pending requests) is dropped in memory, never logged, stored or reported;
- **the type never reaches the calendar**: each type maps to a title template, and by default every
  kept type looks like annual leave.
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass

from .config import Config

FULL, AM, PM = "FULL", "HALF_AM", "HALF_PM"
# annotate=t names each day part; the ids are what the events carry.
PART_NAMES = {"all day": FULL, "am": AM, "pm": PM}


class LeaveError(RuntimeError):
    pass


@dataclass(frozen=True)
class Person:
    id: str
    name: str            # as the calendar shows it: People First's "First Last", or the [names] override


@dataclass(frozen=True)
class Absence:
    id: str              # People First's absence id: the calendar entry is keyed on it
    person: Person
    start: dt.date
    end: dt.date         # inclusive
    start_part: str      # FULL, HALF_AM or HALF_PM
    end_part: str
    template: str        # the type's title template ({name})

    @property
    def halves(self) -> tuple:
        """(single-day half, first-day half, last-day half), each None for a whole day.

        One day: AM or PM if only one half is off (both halves, or FULL, is a whole day).
        Several days: the first day's part says when it starts (PM: from the afternoon), the last day's
        when it ends (AM: after the morning); anything else is a whole day."""
        if self.start == self.end:
            parts = {self.start_part, self.end_part} - {FULL}
            return (parts.pop() if len(parts) == 1 else None), None, None
        return None, (PM if self.start_part == PM else None), (AM if self.end_part == AM else None)

    def title(self, config: Config) -> str:
        title = self.template.format(name=self.person.name)
        single, first, last = self.halves
        t = config.titles
        if single == AM:
            title = t.am.format(title=title)
        elif single == PM:
            title = t.pm.format(title=title)
        if first:
            title = t.from_pm.format(title=title)
        if last:
            title = t.to_am.format(title=title)
        return title


def display_name(config: Config, member: dict) -> str:
    full = " ".join(p for p in ((member.get("firstName") or "").strip(), (member.get("lastName") or "").strip()) if p)
    return config.names.get(full, full)


def _lookup(meta: dict, key: str) -> dict:
    """annotate=t's {id: value} for one field, e.g. events.eventTypeId."""
    return {v.get("id"): str(v.get("value") or "") for v in (meta.get(key) or {}).get("values") or []}


def _part(value, names: dict) -> str:
    if value in (FULL, AM, PM):
        return value
    named = PART_NAMES.get(names.get(value, "").strip().lower())
    if named:
        return named
    raise LeaveError(f"People First gave a day part it doesn't know ({value!r}), so leave can't be read safely.")


def absences(config: Config, person: Person, body: dict) -> list[Absence]:
    """The kept absences in one person's events listing (annotate=t). Raises LeaveError if the listing
    has changed shape, rather than guess: a guess could remove someone's leave from the calendar."""
    if not isinstance(body, dict) or not isinstance(body.get("data"), dict) or "events" not in body["data"]:
        raise LeaveError("People First's events listing has changed shape (no data.events).")
    meta = body.get("meta") or {}
    types = _lookup(meta, "events.eventTypeId")
    if not types:
        raise LeaveError("People First's events listing didn't name the absence types (annotate=t has changed?), "
                         "so leave can't be read safely.")
    wanted = {type_id: config.types[name] for type_id, name in types.items() if name in config.types}
    starts, ends = _lookup(meta, "events.startTypeId"), _lookup(meta, "events.endTypeId")
    out = []
    for event in body["data"]["events"] or []:
        if event.get("eventTypeId") not in wanted or event.get("state") not in config.states:
            continue   # dropped unread: not logged, not stored
        try:
            out.append(Absence(id=str(event["eventId"]), person=person,
                               start=dt.date.fromisoformat(str(event["startDate"])[:10]),
                               end=dt.date.fromisoformat(str(event["endDate"])[:10]),
                               start_part=_part(event.get("startTypeId") or FULL, starts),
                               end_part=_part(event.get("endTypeId") or FULL, ends),
                               template=wanted[event["eventTypeId"]]))
        except (KeyError, ValueError) as error:
            raise LeaveError(f"An absence in People First's listing couldn't be read ({type(error).__name__}).") from error
    return out


def missing_types(config: Config, body: dict) -> list[str]:
    """Configured types that People First doesn't have at all: almost certainly a typo in [types]."""
    names = set(_lookup(body.get("meta") or {}, "events.eventTypeId").values())
    return [name for name in config.types if names and name not in names]


def read(config: Config, api, today: dt.date, log=print) -> tuple[list[Person], list[Absence]]:
    """Your reports (to config.depth levels) and their kept absences from today to days_ahead. Any
    failure raises: a partial answer must never be mistaken for leave that was cancelled."""
    end = today + dt.timedelta(days=config.days_ahead)
    me = api.me()
    people, queue, seen = [], [(me, 0)], {me}
    while queue:
        manager, level = queue.pop(0)
        if level >= config.depth:
            continue
        for member in api.team_members(manager):
            pid = str(member["personId"])
            if pid in seen:
                continue
            seen.add(pid)
            people.append(Person(pid, display_name(config, member)))
            queue.append((pid, level + 1))   # only read if depth allows another level
    found, checked = [], False
    for person in people:
        body = api.events(person.id, today, end)
        if not checked:
            unknown = missing_types(config, body)
            if unknown:
                raise LeaveError(f"People First has no absence type called {', '.join(map(repr, unknown))}: "
                                 "check [types] in the settings.")
            checked = True
        found += absences(config, person, body)
    log(f"people first: {len(people)} people, {len(found)} absences kept")
    return people, found
