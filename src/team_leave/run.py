"""One locked run: read People First, plan, write (unless a dry run), run the hook, and notify problems.

**Notifications are for problems only**, once per problem, never every 15 minutes:
- **a sign-in is needed** (Microsoft or People First): at once;
- **People First or Graph failing three runs in a row** (FAILURES_BEFORE_NOTICE): one blip, such as a
  slow People First page while the laptop wakes, isn't worth a notification;
- **a safety stop** (see sync.py): at once.
The next success clears the problem, so a recurrence is notified again.

**The state file holds no names, dates or types**: the failure count, the problem last notified, and
the last run's time and counts. The calendar is the record of what was written.

The after-run hook gets a JSON report on stdin after a run that changed the calendar, the same way
org-directory's and doc-filer's do, with names and dates (never types):

    {"config": "...", "dry_run": false, "summary": "1 added, 0 changed, 0 removed, 5 unchanged",
     "added": [{"name": "Alex Example", "title": "Alex Example annual leave", "start": "2026-12-24",
                "end": "2026-12-31", "text": "Alex Example annual leave 24/12 to 31/12"}],
     "changed": [... "was": "..."], "adopted": [... "was": "..."], "removed": [{"title", "start", "end", "text"}], ...}

It runs from the settings file's folder, with no window.
"""

from __future__ import annotations

import datetime as dt
import json
import subprocess

import people_first
from m365_graph import Client
from m365_graph import SignInNeeded as GraphSignIn
from win_background import NO_WINDOW, exclusive, key, notify
from win_background import logger as background_logger

from . import calendar as cal, leave, sync
from .config import LOCAL, Config

FAILURES_BEFORE_NOTICE = 3
LOCK_WAIT = 600


def client(config: Config, interactive: bool | None = None) -> Client:
    kwargs = {"cache_path": config.msal_cache} if config.msal_cache else {}
    return Client(tenant=config.tenant, login_hint=config.login_hint, scopes=cal.SCOPES, interactive=interactive,
                  sign_in_hint="At a console, run: team-leave sign-in", **kwargs)


def logger(config: Config, echo: bool = True):
    return background_logger(config.log_file, echo=echo, stamp_echo=False)


def load_state(config: Config) -> dict:
    try:
        return json.loads(config.state_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def save_state(config: Config, state: dict) -> None:
    config.state_file.parent.mkdir(parents=True, exist_ok=True)
    temp = config.state_file.with_suffix(".tmp")
    temp.write_text(json.dumps(state, indent=1), encoding="utf-8")
    temp.replace(config.state_file)


def in_hours(config: Config, now: dt.datetime) -> bool:
    if config.weekdays and now.weekday() >= 5:
        return False
    return config.hours[0] <= now.strftime("%H:%M") < config.hours[1]


def read_people_first(config: Config, today: dt.date, log, headless: bool = True):
    with people_first.session(config.people_first, log=log, headless=headless, lock_wait=LOCK_WAIT) as api:
        return leave.read(config, api, today, log=log)


def sync_once(config: Config, dry_run: bool = False, adopt: bool = False, max_deletes: int | None = None,
              today: dt.date | None = None, log=print, graph=None, read=None) -> dict:
    """Read, plan and (unless dry_run) write. Raises on any failure or safety stop, having written nothing
    for a failed read or a stop."""
    today = today or dt.date.today()
    people, absences = (read or read_people_first)(config, today, log)
    calendar = cal.Calendar(graph or client(config), config)
    plan = sync.plan(config, people, absences, calendar, today, adopt=adopt, max_deletes=max_deletes,
                     survey=dry_run)
    log(f"plan: {len(plan.create)} to create, {len(plan.update)} to update, {len(plan.adopt)} to adopt, "
        f"{len(plan.delete)} to delete, {plan.unchanged} unchanged, {len(plan.by_hand)} already in by hand")
    if not dry_run:
        sync.apply(plan, calendar, log=log)
    return sync.report(config, plan, dry_run)


def problem(error: Exception) -> str:
    if isinstance(error, (GraphSignIn, people_first.SignInNeeded)):
        return "sign-in"
    if isinstance(error, sync.SafetyStop):
        return "safety"
    return "failure"


def run(config: Config, dry_run: bool = False, adopt: bool = False, hook: bool = False, scheduled: bool = False,
        max_deletes: int | None = None, log=print, graph=None, read=None, today: dt.date | None = None,
        alert=notify) -> dict | None:
    """A locked sync with the state kept and problems notified (when scheduled). Returns the report, or
    None if a scheduled run fell outside working hours. Re-raises the error after recording it."""
    if scheduled and not in_hours(config, dt.datetime.now()):
        return None
    with exclusive(f"team-leave-run-{key(config.path)}", LOCK_WAIT, LOCAL, what=f"team-leave sync for {config.path}"):
        state = load_state(config)
        try:
            report = sync_once(config, dry_run=dry_run, adopt=adopt, max_deletes=max_deletes, today=today,
                               log=log, graph=graph or client(config, interactive=False if scheduled else None),
                               read=read)
        except Exception as error:
            kind = problem(error)
            state["failures"] = state.get("failures", 0) + 1
            state["last_error"] = f"{type(error).__name__}: {str(error)[:300]}"
            state["last_run"] = dt.datetime.now().isoformat(timespec="seconds")
            log(f"sync failed ({kind}): {str(error)[:300]}")
            due = kind in ("sign-in", "safety") or state["failures"] >= FAILURES_BEFORE_NOTICE
            if scheduled and due and state.get("notified") != kind:
                titles = {"sign-in": "team-leave needs a sign-in", "safety": "team-leave stopped: nothing was changed",
                          "failure": f"team-leave has failed {state['failures']} times in a row"}
                alert(titles[kind], [str(error)[:400]])
                state["notified"] = kind
            if not dry_run:
                save_state(config, state)
            raise
        log(report["summary"] + (" (dry run: nothing written)" if dry_run else ""))
        if not dry_run:
            save_state(config, {"last_run": dt.datetime.now().isoformat(timespec="seconds"), "failures": 0,
                                "last_result": report["summary"]})
    changed = report["added"] or report["changed"] or report["removed"] or report["adopted"]
    if hook and not dry_run and changed and config.after_run:
        after_run(config, report, log)
    return report


def after_run(config: Config, report: dict, log=print) -> None:
    log(f"after-run hook: {' '.join(config.after_run)}")
    try:
        done = subprocess.run(config.after_run, input=json.dumps(report), cwd=config.path.parent, capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=600, creationflags=NO_WINDOW)
        for line in (done.stdout + done.stderr).splitlines():
            if line.strip():
                log(f"  hook: {line.strip()[:200]}")
        if done.returncode:
            log(f"after-run hook exited {done.returncode}")
    except Exception as error:
        log(f"after-run hook failed: {error}")
