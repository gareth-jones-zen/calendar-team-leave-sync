"""The settings file: whose leave, how it looks in your calendar, and when it runs.

    login_hint = "you@example.com"          # optional: your Microsoft sign-in
    tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
    msal_cache = "..."                      # optional: share another tool's sign-in
    state_file = "..."                      # optional: failure counts and the last run (no names or dates)
    log_file = "..."                        # optional
    after_run = ["python", "hook.py"]       # optional: run after a sync that changed the calendar (JSON on stdin)
    days_ahead = 365                        # optional: how far ahead to copy leave
    max_deletes = 5                         # optional: more removals than this in one run stops it
    every_minutes = 15                      # optional: autostart's interval (also at logon)
    hours = "07:00-19:00"                   # optional: scheduled runs outside these hours do nothing
    weekdays = true                         # optional: ...nor at weekends

    [people_first]
    url = "https://yourorg.people-first.com"
    tenant_code = "yourorg"                 # optional: default, the first part of url
    account = "you@example.com"             # optional: the account to pick at sign-in (default login_hint)
    edge_profile = "..."                    # optional: Edge profile; share another tool's to share its sign-in
    depth = 1                               # optional: levels of reports (1: direct reports only)
    states = ["Approved"]                   # optional: which absences count (pending is "AwaitingApproval")

    [types]                                 # optional: People First absence type -> calendar title
    "Holiday" = "{name} annual leave"       # the default: annual leave only

    [titles]                                # optional: how half days are marked ({title} is the type's title)
    am = "{title} (AM)"                     # a morning off
    pm = "{title} (PM)"                     # an afternoon off
    from_pm = "{title} (from PM)"           # several days, starting in the afternoon
    to_am = "{title} (to AM)"               # several days, ending at midday

    [event]                                 # optional: the calendar entry itself
    categories = ["Planned"]
    reminder = false                        # or minutes before, e.g. 1080 for 6pm the day before
    sensitivity = "normal"                  # normal, personal, private or confidential
    show_as = "free"                        # free, tentative, busy, oof or workingElsewhere
    time_zone = "Europe/London"
    body = "Synced from People First by team-leave. Changes made here are overwritten."

    [names]                                 # optional: People First's "First Last" -> the name to use
    "Robert Example" = "Bob Example"

Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.
"""

from __future__ import annotations

import hashlib
import os
import pathlib
import re
import tomllib
from dataclasses import dataclass, field

import people_first

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "team-leave"
DEFAULT_CONFIG = APPDATA / "team-leave" / "config.toml"

TEMPLATE = """\
# team-leave settings. See the README.
# login_hint = "you@example.com"

[people_first]
url = "https://yourorg.people-first.com"
"""

SHOW_AS = ("free", "tentative", "busy", "oof", "workingElsewhere")
SENSITIVITY = ("normal", "personal", "private", "confidential")
DEFAULT_BODY = "Synced from People First by team-leave. Changes made here are overwritten."


@dataclass
class Titles:
    am: str = "{title} (AM)"
    pm: str = "{title} (PM)"
    from_pm: str = "{title} (from PM)"
    to_am: str = "{title} (to AM)"


@dataclass
class Event:
    categories: list = field(default_factory=lambda: ["Planned"])
    reminder: int | None = None             # minutes before; None: no reminder
    sensitivity: str = "normal"
    show_as: str = "free"
    time_zone: str = "Europe/London"
    body: str = DEFAULT_BODY


@dataclass
class Config:
    path: pathlib.Path
    people_first: people_first.Settings
    login_hint: str | None = None
    tenant: str = "organizations"
    msal_cache: pathlib.Path | None = None
    state_file: pathlib.Path = LOCAL / "state.json"
    log_file: pathlib.Path = LOCAL / "team-leave.log"
    after_run: list = field(default_factory=list)
    days_ahead: int = 365
    max_deletes: int = 5
    every_minutes: int = 15
    hours: tuple = ("07:00", "19:00")
    weekdays: bool = True
    depth: int = 1
    states: list = field(default_factory=lambda: ["Approved"])
    types: dict = field(default_factory=lambda: {"Holiday": "{name} annual leave"})
    titles: Titles = field(default_factory=Titles)
    event: Event = field(default_factory=Event)
    names: dict = field(default_factory=dict)


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return path if path.is_absolute() else (base / path).resolve()


def _time(value: str, path) -> str:
    if not re.fullmatch(r"([01]\d|2[0-3]):[0-5]\d", value):
        raise ValueError(f"{value!r} in {path} should be a time such as \"07:00\".")
    return value


def load(path: pathlib.Path | str | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("TEAM_LEAVE_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `team-leave config` to create one.")
    return parse(tomllib.loads(path.read_text(encoding="utf-8")), path)


def parse(raw: dict, path: pathlib.Path) -> Config:
    base = path.parent
    key = hashlib.sha256(str(path).lower().encode()).hexdigest()[:12]
    pf = raw.get("people_first") or {}
    url = str(pf.get("url", "")).rstrip("/")
    if not url.startswith("https://"):
        raise ValueError(f"[people_first] in {path} needs url, such as \"https://yourorg.people-first.com\".")
    tenant_code = pf.get("tenant_code") or people_first.default_tenant_code(url)
    if not tenant_code:
        raise ValueError(f"[people_first] in {path} needs tenant_code: it couldn't be worked out from the url.")
    settings = people_first.Settings(
        url=url, tenant_code=tenant_code, account=pf.get("account") or raw.get("login_hint") or "",
        edge_profile=_path(pf["edge_profile"], base) if pf.get("edge_profile") else LOCAL / "edge-profile",
        sign_in_hint="At a console, run: team-leave sign-in")

    hours = str(raw.get("hours", "07:00-19:00")).split("-")
    if len(hours) != 2:
        raise ValueError(f"hours in {path} should look like \"07:00-19:00\".")
    config = Config(
        path=path, people_first=settings, login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
        msal_cache=_path(raw["msal_cache"], base) if raw.get("msal_cache") else None,
        state_file=_path(raw["state_file"], base) if raw.get("state_file") else LOCAL / f"state-{key}.json",
        log_file=_path(raw["log_file"], base) if raw.get("log_file") else LOCAL / "team-leave.log",
        after_run=list(raw.get("after_run", [])), days_ahead=int(raw.get("days_ahead", 365)),
        max_deletes=int(raw.get("max_deletes", 5)), every_minutes=int(raw.get("every_minutes", 15)),
        hours=(_time(hours[0].strip(), path), _time(hours[1].strip(), path)), weekdays=bool(raw.get("weekdays", True)),
        depth=int(pf.get("depth", 1)), states=list(pf.get("states", ["Approved"])), names=dict(raw.get("names", {})))
    if not 1 <= config.days_ahead <= 730:
        raise ValueError(f"days_ahead in {path} should be between 1 and 730.")
    if config.every_minutes < 1:
        raise ValueError(f"every_minutes in {path} should be at least 1.")
    if config.depth < 1:
        raise ValueError(f"depth under [people_first] in {path} should be at least 1.")

    if "types" in raw:
        config.types = {str(k): str(v) for k, v in raw["types"].items()}
    if not config.types:
        raise ValueError(f"[types] in {path} is empty: name at least one People First absence type.")
    for name, title in config.types.items():
        if "{name}" not in title:
            raise ValueError(f"The title for {name!r} under [types] in {path} needs {{name}}.")

    titles = raw.get("titles") or {}
    config.titles = Titles(**{k: str(titles.get(k, getattr(Titles, k))) for k in ("am", "pm", "from_pm", "to_am")})
    for k in ("am", "pm", "from_pm", "to_am"):
        if "{title}" not in getattr(config.titles, k):
            raise ValueError(f"{k} under [titles] in {path} needs {{title}}.")

    ev = raw.get("event") or {}
    reminder = ev.get("reminder", False)
    if reminder is True or (reminder is not False and not isinstance(reminder, int)):
        raise ValueError(f"reminder under [event] in {path} should be false, or minutes before (a number).")
    config.event = Event(categories=list(ev.get("categories", ["Planned"])),
                         reminder=None if reminder is False else int(reminder),
                         sensitivity=str(ev.get("sensitivity", "normal")), show_as=str(ev.get("show_as", "free")),
                         time_zone=str(ev.get("time_zone", "Europe/London")), body=str(ev.get("body", DEFAULT_BODY)))
    if config.event.sensitivity not in SENSITIVITY:
        raise ValueError(f"sensitivity under [event] in {path} should be one of {', '.join(SENSITIVITY)}.")
    if config.event.show_as not in SHOW_AS:
        raise ValueError(f"show_as under [event] in {path} should be one of {', '.join(SHOW_AS)}.")
    return config


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
