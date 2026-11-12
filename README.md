# team-leave

**Your direct reports' approved leave, copied from People First (MHR) into your Outlook calendar, and
kept in step**, so you no longer type it in by hand. Leave that's approved appears; leave that's
changed is updated; leave that's cancelled or rejected disappears.

People First's own calendar link (Cronofy) only puts each person's leave in *their own* calendar, not
their manager's, so a manager otherwise copies it by hand.

```
team-leave sync --dry-run        what it would add, change and remove
team-leave sync                  do it
team-leave autostart on          every 15 minutes during the day, and at logon
```

## Install

```
pipx install --preinstall git+https://github.com/gareth-jones-zen/m365-graph --preinstall git+https://github.com/gareth-jones-zen/win-background --preinstall git+https://github.com/gareth-jones-zen/people-first-client git+https://github.com/gareth-jones-zen/team-leave
```

None of [m365-graph](https://github.com/gareth-jones-zen/m365-graph) (Microsoft sign-in and Graph),
[win-background](https://github.com/gareth-jones-zen/win-background) (the scheduled task, notifications,
locks and the log) and [people-first-client](https://github.com/gareth-jones-zen/people-first-client)
(People First's sign-in) is on PyPI, so they're installed first. Needs Windows, Edge and Python 3.11+.

## Getting started

```
team-leave config                # creates %APPDATA%\team-leave\config.toml; edit it
team-leave sign-in               # Microsoft (a browser), then People First (a window)
team-leave sync --dry-run        # check the plan
team-leave sync --adopt --dry-run   # ...if you've been entering leave by hand
team-leave sync                  # or sync --adopt
team-leave autostart on
```

A minimal settings file:

```toml
login_hint = "you@example.com"

[people_first]
url = "https://yourorg.people-first.com"
```

## What it reads, and what it keeps

- **People First, as you** (people-first-client): your direct reports (`/hrm/people/{you}/team`), then each one's absences from today to `days_ahead` (`/hrm/people/{id}/events`). **Nothing else.** It never reads `/hrm/absences/{id}`, which carries the reason, or the Calendar API, which carries free text.
- **Only the absence types in `[types]` and the states in `states` are kept** (by default "Holiday" and "Approved"). Everything else (sickness, appointments, pending requests) is dropped as it's read, and never logged, stored or reported.
- **The type never reaches your calendar.** Each type maps to a title template; include a birthday day off, say, and give it the annual-leave title, and nobody can tell.
- **Nothing about anyone's leave is stored on disk.** The calendar is the record. The state file holds the failure count and the last run's counts; the log holds counts and People First absence ids, never names, dates or types.

## What it writes

One entry per absence, in your own calendar: **all day, no attendees, shown as free, no reminder, the
"Planned" category, normal sensitivity**, and a one-line body: *"Synced from People First by
team-leave. Changes made here are overwritten."* All of these are settings.

| Leave | Title |
|---|---|
| Whole days | `Alex Example annual leave` |
| A morning, or an afternoon | `Alex Example annual leave (AM)` / `(PM)` |
| Several days, starting in the afternoon | `Alex Example annual leave (from PM)` |
| Several days, ending at midday | `Alex Example annual leave (to AM)` |

Names are People First's first and last names; `[names]` maps any to the name you use ("Robert
Example" = "Bob Example").

## How it keeps in step

- **Each entry carries two hidden properties** (Outlook single-value extended properties): the People First absence id, and a hash of what was written. **Only entries with them are team-leave's.** It never changes or deletes anything else.
- **Every run recomputes the whole window and compares it with your calendar**: missing entries are created, ones that differ are rewritten (including a title or dates you edited by hand), and ones whose absence has gone are deleted. There's no local list to go stale: lose the state file and nothing happens.
- **The past is never touched.** Entries that ended before today are left as they are, even if the leave changes in People First.
- **A run with nothing to change makes only the reads.**

**Leave you've already entered by hand isn't duplicated.** An entry you organise, with no attendees,
all day, titled `<Name> annual leave` or `<Name> on leave` (half-day marks allowed), on exactly the
same days, stops team-leave creating its own, and so do several such entries that between them cover
every weekday of the leave (entered in pieces around a weekend). **`sync --adopt` takes over the exact
ones** that start today or later: they're stamped as team-leave's and retitled to the settings, so later
changes in People First reach them. Entries in pieces are never adopted; delete them by hand and
team-leave writes one entry. A dry run also lists hand-made entries that match no approved leave.

## Safety stops

**Nothing is written at all** if:
- **any People First read fails**: a failed read must never look like cancelled leave;
- **People First gives no leave at all while team-leave has future entries** ("everything vanished");
- **more than `max_deletes` (default 5) entries would be removed** in one run.

The stop is notified. If the removals are real, check with `sync --dry-run`, then run `sync --max-deletes N` once.

## Running it: `autostart`

`team-leave autostart on` creates a Windows Task Scheduler task, **"team-leave sync"**, that runs
`sync --hook --scheduled` **every `every_minutes` (default 15) and at logon**, as you, with no window and
no admin rights. **Runs outside `hours` (default 07:00-19:00), and at weekends with `weekdays = true`,
exit at once.** One run at a time per settings file. `autostart status` and `team-leave status` say how it's going.

**It notifies only problems, once each:** a sign-in needed (Microsoft or People First), People First or
Graph failing three runs in a row, or a safety stop. The next success clears it.

## After-run hook

`after_run = ["python", "hook.py"]` runs after a run that changed the calendar, from the settings file's
folder, with a JSON report on stdin:

```json
{"config": "...", "dry_run": false, "summary": "1 added, 0 changed, 0 removed, 5 unchanged",
 "added": [{"name": "Alex Example", "title": "Alex Example annual leave", "start": "2026-12-24",
            "end": "2026-12-31", "text": "Alex Example annual leave 24/12 to 31/12"}],
 "changed": [{"...": "...", "was": "Alex Example annual leave 23/12 to 31/12"}],
 "adopted": [], "removed": [{"title": "...", "start": "...", "end": "...", "text": "..."}]}
```

## Settings

Everything else is optional; `config.py` documents each one.

```toml
login_hint = "you@example.com"
tenant = "organizations"          # or your tenant id
msal_cache = "..."                # share another tool's Microsoft sign-in
after_run = ["python", "hook.py"]
days_ahead = 365
max_deletes = 5
every_minutes = 15
hours = "07:00-19:00"
weekdays = true

[people_first]
url = "https://yourorg.people-first.com"
edge_profile = "..."              # share another tool's People First sign-in: the profile's lock makes them take turns
depth = 1                         # 2 includes your reports' reports
states = ["Approved"]             # add "AwaitingApproval" for pending requests

[types]                           # People First absence type -> title
"Holiday" = "{name} annual leave"

[titles]
am = "{title} (AM)"
pm = "{title} (PM)"
from_pm = "{title} (from PM)"
to_am = "{title} (to AM)"

[event]
categories = ["Planned"]
reminder = false                  # or minutes before
sensitivity = "normal"            # or private
show_as = "free"
time_zone = "Europe/London"

[names]
"Robert Example" = "Bob Example"
```

**Microsoft permissions:** `Calendars.ReadWrite` (delegated), signed in as Microsoft's own Graph
Command Line Tools app unless you set your own.

## Before sharing it

It reads People First with a token taken from the web app (see people-first-client). That's fine for
your own use; **clear it with your HR system's owners and information security before others use it.**
A central alternative is People First's webhooks, set up by its administrator, feeding a small service
that writes to managers' calendars.

## Tests

```
python -m unittest discover -s tests
```
