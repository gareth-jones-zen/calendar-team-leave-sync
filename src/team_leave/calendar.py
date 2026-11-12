"""Your Outlook calendar, through Microsoft Graph: the entries team-leave owns, and hand-made ones.

**An entry is team-leave's only if it carries its extended property** (a hidden, single-value MAPI
property holding the People First absence id), plus a second holding a hash of what was written.
**The calendar is the source of truth**: there's no local list of entries, so a lost state file is
harmless, and an entry deleted by hand is simply written again. Entries without the property are
never changed or deleted, unless `sync --adopt` takes one over (see sync.py).
"""

from __future__ import annotations

import datetime as dt
import hashlib
import json
import re
import urllib.parse
from dataclasses import dataclass

from .config import Config
from .leave import Absence

SCOPES = ["Calendars.ReadWrite"]
PROPERTY_SET = "{bd639353-3a69-4233-90ad-38c8c468272a}"   # team-leave's own; never change it
ABSENCE_PROP = f"String {PROPERTY_SET} Name team-leave.absence"
HASH_PROP = f"String {PROPERTY_SET} Name team-leave.hash"
SELECT = "subject,start,end,isAllDay,showAs,categories,sensitivity,isReminderOn,isOrganizer,attendees,isCancelled,type"


@dataclass
class Entry:
    """A calendar entry as read: dates are the first day and the last day (inclusive)."""
    id: str
    subject: str
    start: dt.date
    end: dt.date
    all_day: bool
    absence: str = ""          # the People First absence id, if team-leave owns it
    hash: str = ""
    organiser: bool = True
    attendees: int = 0
    cancelled: bool = False
    kind: str = "singleInstance"


def _date(value: dict) -> dt.datetime:
    return dt.datetime.fromisoformat((value or {}).get("dateTime", "")[:19])


def entry(item: dict) -> Entry:
    start, end = _date(item["start"]), _date(item["end"])
    props = {p.get("id", "").lower(): p.get("value", "") for p in item.get("singleValueExtendedProperties") or []}
    # All-day entries end at midnight the day after; a timed one ends on the day its end falls in.
    last = (end - dt.timedelta(minutes=1)).date() if end > start else start.date()
    return Entry(id=item["id"], subject=item.get("subject") or "", start=start.date(), end=last,
                 all_day=bool(item.get("isAllDay")), absence=props.get(ABSENCE_PROP.lower(), ""),
                 hash=props.get(HASH_PROP.lower(), ""), organiser=item.get("isOrganizer", True),
                 attendees=len(item.get("attendees") or []), cancelled=bool(item.get("isCancelled")),
                 kind=item.get("type") or "singleInstance")


def body(config: Config, absence: Absence) -> dict:
    """What the entry should be. No attendees (added on create only), and never the type or a reason."""
    ev = config.event
    out = {"subject": absence.title(config),
           "start": {"dateTime": f"{absence.start.isoformat()}T00:00:00", "timeZone": ev.time_zone},
           "end": {"dateTime": f"{(absence.end + dt.timedelta(days=1)).isoformat()}T00:00:00", "timeZone": ev.time_zone},
           "isAllDay": True, "showAs": ev.show_as, "sensitivity": ev.sensitivity, "categories": list(ev.categories),
           "isReminderOn": ev.reminder is not None,
           "body": {"contentType": "text", "content": ev.body}}
    if ev.reminder is not None:
        out["reminderMinutesBeforeStart"] = ev.reminder
    return out


def digest(payload: dict) -> str:
    return hashlib.sha256(json.dumps(payload, sort_keys=True).encode()).hexdigest()[:16]


def stamped(payload: dict, absence_id: str) -> dict:
    """The payload with team-leave's properties: what a create, update or adoption writes."""
    return {**payload, "singleValueExtendedProperties": [{"id": ABSENCE_PROP, "value": absence_id},
                                                        {"id": HASH_PROP, "value": digest(payload)}]}


def differs(current: Entry, payload: dict) -> bool:
    """Whether an owned entry needs rewriting: what was written has changed, or someone edited the
    title or dates by hand (the body says edits are overwritten)."""
    want_start = dt.date.fromisoformat(payload["start"]["dateTime"][:10])
    want_end = dt.date.fromisoformat(payload["end"]["dateTime"][:10]) - dt.timedelta(days=1)
    return (current.hash != digest(payload) or current.subject != payload["subject"] or not current.all_day
            or current.start != want_start or current.end != want_end)


def hand_pattern(name: str) -> re.Pattern:
    """A hand-made leave entry for this person: "<Name> annual leave" or "<Name> on leave", optionally with
    the half-day marks the titles use: (AM), (PM), (from PM), (to AM), with or without brackets."""
    return re.compile(rf"^\s*{re.escape(name)}\s+(annual leave|on leave)"
                      rf"(\s*\(?\s*((from|to)\s+)?(am|pm)\s*\)?)*\s*$", re.I)


class Calendar:
    """The reads and writes, against a m365_graph.Client (or a fake with the same request/pages)."""

    def __init__(self, graph, config: Config):
        self.graph = graph
        self.headers = {"Prefer": f'outlook.timezone="{config.event.time_zone}"'}

    def owned(self) -> list[Entry]:
        """Every entry carrying team-leave's property, past ones included (the caller leaves those alone)."""
        flt = urllib.parse.quote(f"singleValueExtendedProperties/Any(ep: ep/id eq '{ABSENCE_PROP}' and ep/value ne null)")
        expand = urllib.parse.quote(f"singleValueExtendedProperties($filter=id eq '{ABSENCE_PROP}' or id eq '{HASH_PROP}')")
        url = f"/me/events?$filter={flt}&$expand={expand}&$select={SELECT}&$top=100"
        return [entry(item) for item in self.graph.pages(url, scopes=SCOPES, headers=self.headers)]

    def between(self, first: dt.date, last: dt.date) -> list[Entry]:
        """Everything in the calendar from first to last (inclusive), with team-leave's property where set."""
        expand = urllib.parse.quote(f"singleValueExtendedProperties($filter=id eq '{ABSENCE_PROP}')")
        url = (f"/me/calendarView?startDateTime={first.isoformat()}T00:00:00"
               f"&endDateTime={(last + dt.timedelta(days=1)).isoformat()}T00:00:00"
               f"&$expand={expand}&$select={SELECT}&$top=500")
        return [entry(item) for item in self.graph.pages(url, scopes=SCOPES, headers=self.headers)]

    def create(self, payload: dict) -> str:
        return self.graph.request("/me/events", method="POST", body={**payload, "attendees": []},
                                  scopes=SCOPES, headers=self.headers).get("id", "")

    def update(self, event_id: str, payload: dict) -> None:
        self.graph.request(f"/me/events/{event_id}", method="PATCH", body=payload, scopes=SCOPES, headers=self.headers)

    def delete(self, event_id: str) -> None:
        self.graph.request(f"/me/events/{event_id}", method="DELETE", scopes=SCOPES, headers=self.headers)
