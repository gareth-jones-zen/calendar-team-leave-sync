# calendar-team-leave-sync

**Your direct reports' approved leave, copied from People First (MHR) into your Outlook calendar, and
kept in step**, so you no longer type it in by hand. Leave that's approved appears; leave that's
changed is updated; leave that's cancelled or rejected disappears. **Optionally, routine leave requests
are approved by rules you set**, and the rest are raised with you, with who's in each day.

People First's own calendar link (Cronofy) only puts each person's leave in *their own* calendar, not
their manager's, so a manager otherwise copies it by hand.

```
calendar-team-leave-sync sync --dry-run        what it would add, change and remove
calendar-team-leave-sync sync                  do it
calendar-team-leave-sync autostart on          every 15 minutes during the day, and at logon
calendar-team-leave-sync approvals             with [approvals]: what the rules make of each waiting request
calendar-team-leave-sync approve ID            approve one request yourself
calendar-team-leave-sync digest                People First's notifications email (JSON on stdin): 0 if it's only leave requests
                                 calendar-team-leave-sync has seen (and likes), 75 if one isn't seen yet, 1 for anything else
```

## Install

Windows (x64) with Microsoft Edge, which Windows has; nothing else.

1. Download `calendar-team-leave-sync-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/calendar-team-leave-sync/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\calendar-team-leave-sync`. The scheduled task (`autostart`) runs the program from there.
3. Run the commands below from that folder, or add it to your `PATH`.

It holds two programs: `calendar-team-leave-sync.exe`, for a console, and `calendar-team-leave-sync-background.exe`, the same program with no
window, which the scheduled task runs.

### Upgrading from team-leave (1.6.x and earlier)

The tool was called `team-leave` until 1.7.0. Its calendar entries keep their hidden `team-leave.absence` and
`team-leave.hash` properties, so the renamed tool recognises them and never duplicates them. To move an install:
`team-leave autostart off`; unzip 1.7.0 to `%LOCALAPPDATA%\Programs\calendar-team-leave-sync`; point `log_file` and
`state_file` at the new names (or move the files; the state also holds the leave requests already seen); run
`calendar-team-leave-sync sync --dry-run`, which should show nothing to add, change or remove; then
`calendar-team-leave-sync autostart on` and delete the old folder. The default body text still names team-leave, so no entry is rewritten.

## Getting started

```
calendar-team-leave-sync config                # creates %APPDATA%\calendar-team-leave-sync\config.toml; edit it
calendar-team-leave-sync sign-in               # Microsoft (a browser), then People First (a window)
calendar-team-leave-sync sync --dry-run        # check the plan
calendar-team-leave-sync sync --adopt --dry-run   # ...if you've been entering leave by hand
calendar-team-leave-sync sync                  # or sync --adopt
calendar-team-leave-sync autostart on
```

A minimal settings file:

```toml
login_hint = "you@example.com"

[people_first]
url = "https://yourorg.people-first.com"
```

## What it reads, and what it keeps

- **People First, as you**: your direct reports (`/hrm/people/{you}/team`), then each one's absences from today to the end of the look-ahead window (`days_ahead`, or `long_range_months_ahead` calendar months) (`/hrm/people/{id}/events`). **Nothing else.** It never reads `/hrm/absences/{id}`, which carries the reason, or the Calendar API, which carries free text.
- **Only the absence types in `[types]` and the states in `states` are kept** (by default "Holiday" and "Approved"). Everything else (sickness, appointments, pending requests) is dropped as it's read, and never logged, stored or reported.
- **The type never reaches your calendar.** Each type maps to a title template; include a birthday day off, say, and give it the annual-leave title, and nobody can tell.
- **Nothing about anyone's leave is stored on disk.** The calendar is the record. The state file holds the failure count and the last run's counts; the log holds counts and People First absence ids, never names, dates or types.
- **With `[approvals]`** (below), the same listings give a little more, kept in memory only: the requests waiting for approval of its types, and who's away when (any absence that isn't rejected, as person and dates: never its type or state), for the cover rule. Each waiting request's history (`/hrm/absences/{id}/absenceChanges`) and approval template are read too. While a request is waiting, the state file records its id, the person's People First id, its dates and what was decided; the record goes once it's no longer waiting.

## What it writes

One entry per absence, in your own calendar: **all day, no attendees, shown as free, no reminder, the
"Planned" category, normal sensitivity**, and a one-line body: *"Synced from People First by
team-leave. Changes made here are overwritten."* (the old name, kept: the body is part of each entry's hash). All of
these are settings.

| Leave | Title |
|---|---|
| Whole days | `Alex Example annual leave` |
| A morning, or an afternoon | `Alex Example annual leave (AM)` / `(PM)` |
| Several days, starting in the afternoon | `Alex Example annual leave (from PM)` |
| Several days, ending at midday | `Alex Example annual leave (to AM)` |

Names are People First's first and last names; `[names]` maps any to the name you use ("Robert
Example" = "Bob Example").

## How it keeps in step

- **Each entry carries two hidden properties** (Outlook single-value extended properties): the People First absence id, and a hash of what was written. **Only entries with them are calendar-team-leave-sync's.** It never changes or deletes anything else.
- **Every run recomputes the whole window and compares it with your calendar**: missing entries are created, ones that differ are rewritten (including a title or dates you edited by hand), and ones whose absence has gone are deleted. There's no local list to go stale: lose the state file and nothing happens.
- **The past is never touched.** Entries that ended before today are left as they are, even if the leave changes in People First.
- **A run with nothing to change makes only the reads.**

**Leave you've already entered by hand isn't duplicated.** An entry you organise, with no attendees,
all day, titled `<Name> annual leave` or `<Name> on leave` (half-day marks allowed), on exactly the
same days, stops calendar-team-leave-sync creating its own, and so do several such entries that between them cover
every weekday of the leave (entered in pieces around a weekend). **`sync --adopt` takes over the exact
ones** that start today or later: they're stamped as calendar-team-leave-sync's and retitled to the settings, so later
changes in People First reach them. Entries in pieces are never adopted; delete them by hand and
calendar-team-leave-sync writes one entry. A dry run also lists hand-made entries that match no approved leave.

## Safety stops

**Nothing is written at all** if:
- **any People First read fails**: a failed read must never look like cancelled leave;
- **People First gives no leave at all while calendar-team-leave-sync has future entries** ("everything vanished");
- **more than `max_deletes` (default 5) entries would be removed** in one run.

The stop is notified. If the removals are real, check with `sync --dry-run`, then run `sync --max-deletes N` once.

## Leave approvals: `[approvals]`

**Routine leave requests are approved by rules, and the rest are raised with you**, with every reason and who's in on
each working day. **Nothing is ever rejected.** With no `[approvals]` section, none of this happens.

```toml
[approvals]
act = false                # false (the default): preview, approving nothing
max_per_run = 5            # never more approvals than this in one run
types = ["Holiday", "My Birthday"]
min_in = 1                 # at least this many of the team in on every working day it covers
max_working_days = 10      # longer: ask
notice_days = 7            # less notice than this: ask...
short_notice_ok_days = 2   # ...unless it's this many working days or fewer
protected = [ { start = "2027-02-08", end = "2027-02-09", why = "cutover" } ]   # overlapping: ask
no_cover_needed = [ { start = "2026-12-24", end = "2027-01-01", why = "Christmas" } ]   # min_in not checked on these days
notify = true              # a notification when a request needs you, once per request
```

**A request is approved only if every rule holds:**
- its type is one of `types`;
- it doesn't change leave already approved: its history has no approval, auto-approval or modification;
- on every working day it covers, at least `min_in` of the team are in. **Anyone with an absence that isn't rejected
  counts as away**, approved or still waiting (so two requests that together leave nobody in are both asked), sickness
  included, and a half day counts as a whole one. Working days are Monday to Friday; bank holidays aren't left out,
  which only makes this stricter. **Days in a `no_cover_needed` range aren't checked** (Christmas, when it doesn't
  matter if no one is in), though the cover is still shown;
- it's no more than `max_working_days` working days;
- it's at least `notice_days`' notice (calendar days from today to its first day), **or** `short_notice_ok_days`
  working days or fewer;
- it doesn't overlap a `protected` range;
- its cover can be checked: it doesn't start before today or run past the look-ahead window.

**Each run, before the calendar:** every waiting request is decided. With `act = true`, those the rules approve are
approved, through the write door, up to `max_per_run`: the absence's approval template is read
(`/hrm/absences/{id}/approved/resourcetemplate`), its id filled in, and it's posted back to the template's own link.
Then that person's listing is read again, and **only an absence People First now shows as Approved counts as
approved**. It gets its calendar entry in the same run.

- **An approval is never sent twice.** If the answer is lost (a timeout, a dropped connection, a 5xx), it's recorded
  as unconfirmed and the next run reads it back: approved, it's done; still waiting, it's raised with you ("the
  approval may not have gone through"), and never posted again.
- **A request is raised with you once**, and again only if its dates change. Once raised, it's yours: the rules don't
  approve it later.
- **A failure that certainly didn't happen** (People First refused it, or couldn't be reached) is raised with you too.
- **Preview (`act = false`)** reports what it would approve, once each, and raises the rest; it approves nothing.

`calendar-team-leave-sync approvals` shows what the rules make of each waiting request, its id and who's in each day, and writes
nothing. `calendar-team-leave-sync approve ID` approves one yourself, whatever the rules say (any type), through the write door,
and reads it back; it refuses an absence that isn't waiting for approval in your team. The next sync puts it in your
calendar.

**Before turning `act` on, clear it with HR**: People First records the approval as yours.

## People First's daily email: `digest`

People First emails a daily "Today's notifications" digest, and once calendar-team-leave-sync reads leave requests itself, a digest
of nothing but leave requests tells you nothing new. **`calendar-team-leave-sync digest`** checks one: give it the email as JSON on
standard input (`body` as text), as a [mail-triage](https://github.com/gareth-jones-zen/mail-triage) handler does,
and it answers with an exit code:

- **0:** every notification is a leave request calendar-team-leave-sync has seen, by person and dates, or someone liking a post
  ("… liked: \"Happy Anniversary\"", People First's social notices: noise, but counted so the total adds up), so the
  email can go;
- **75:** one isn't seen yet (try again later: the next sync may read it);
- **1:** anything else (an HR action, your own leave, a cancellation, a count that doesn't add up), so it's raised.

**What a sync has seen** is kept in the state file as `seen`: each waiting request and each approved absence it read,
for 30 days after it last read it. **Only a fingerprint of who and when is kept** (a SHA-256 of the name and the dates,
cut to 16 characters), as the state file never holds names or dates; `digest` makes the same fingerprint from the
email, trying People First's formal name and its `[names]` override. A fingerprint isn't encryption: someone with the
file who guesses a name and dates could confirm them, but nothing in it can be read as it stands.

```toml
[[handler]]                       # in mail-triage's settings
name = 'people-first-digest'
command = ['C:\Users\you\AppData\Local\Programs\calendar-team-leave-sync\calendar-team-leave-sync.exe', '--config', 'calendar-team-leave-sync.toml', 'digest']
then = 'delete'
max_attempts = 8                  # a sync every 15 minutes: two hours to catch up, then it's raised
```

## Running it: `autostart`

`calendar-team-leave-sync autostart on` creates a Windows Task Scheduler task, **"calendar-team-leave-sync sync"**, that runs
`sync --hook --scheduled` **every `every_minutes` (default 15) and at logon**, as you, with no window and
no admin rights. **Runs outside `hours` (default 07:00-19:00), and at weekends with `weekdays = true`,
exit at once.** One run at a time per settings file. `autostart status` and `calendar-team-leave-sync status` say how it's going.

**It notifies only problems, once each:** a sign-in needed (Microsoft or People First), People First or
Graph failing three runs in a row, or a safety stop. The next success clears it.

## After-run hook

`after_run = ["powershell", "-File", "hook.ps1"]` (any command) runs after a run that changed the calendar, or had
something new about approvals, from the settings file's folder, with a JSON report on stdin:

```json
{"config": "...", "dry_run": false, "summary": "1 added, 0 changed, 0 removed, 5 unchanged",
 "added": [{"name": "Alex Example", "title": "Alex Example annual leave", "start": "2026-12-24",
            "end": "2026-12-31", "text": "Alex Example annual leave 24/12 to 31/12"}],
 "changed": [{"...": "...", "was": "Alex Example annual leave 23/12 to 31/12"}],
 "adopted": [], "removed": [{"title": "...", "start": "...", "end": "...", "text": "..."}]}
```

With `[approvals]`, the report also has `approvals`:

```json
"approvals": {"act": true, "summary": "1 approved, 1 with you (1 new)",
  "approved": [{"id": "...", "name": "Alex Example", "start": "2026-12-01", "end": "2026-12-02",
                "text": "Alex Example annual leave 01/12 to 02/12", "working_days": 2, "new": true,
                "reasons": [], "cover": [{"date": "2026-12-01", "in": ["Sam Example"]}]}],
  "would_approve": [], "deferred": [],
  "asked": [{"...": "...", "new": true, "reasons": ["it leaves nobody in on 02/12"]}],
  "unknown": []}
```

`new` is false for anything already reported (a request still with you, a preview already listed); `deferred` is
over `max_per_run` and approved by the next run; `unknown` was sent and isn't confirmed yet. An approval confirmed late
is in `approved` with `"confirmed_late": true`. Journal mode marks what it would have sent `"journal": true`.

## Review queue: `[review]`

With a `[review]` section, the same runs (`sync --hook`, not a dry run, the calendar changed or something new about
approvals) also put what happened in a **review queue**: one JSON file that whatever starts your day (a script, an
assistant's session) reads, shows you and clears. It's
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)'s `ReviewQueue`, so other tools can
share the file.

```toml
[review]
file = "%LOCALAPPDATA%/my-tools/review.json"   # the queue file
source = "calendar-team-leave-sync"                          # the name its items go under
# lock = "my-tools-review"                     # optional: the writers' lock (default: the folder and file name)
```

**One item per change**, each with `start` and `end`:

| Type | Title |
|---|---|
| `added` | `Alex Example annual leave 24/12 to 31/12 added` |
| `changed` | `... changed (was Alex Example annual leave 23/12 to 31/12)` |
| `adopted` | `... taken over from the hand-made entry` |
| `removed` | `... removed (no longer approved in People First)` |

**And with `[approvals]`, one per approval reported for the first time** (`new`), also with `absence` (its People
First id) and `cover` (who's in each working day, as `2026-12-01: Sam Example; 2026-12-02: nobody`):

| Type | Title |
|---|---|
| `approval` | `Alex Example annual leave 01/12 to 02/12: approved by rule` |
| `approval-preview` | `...: would be approved (preview)` |
| `approval-needed` | `...: needs your decision (it leaves nobody in on 02/12)` |
| `approval-unconfirmed` | `...: approval sent, not confirmed yet` |

- **The same item twice is kept once**, so a change reported again doesn't queue it again.
- **The items name people and dates**, so keep the queue file out of any repo. The log only counts them.
- Writes take a lock between processes and replace the file atomically. If the queue can't be written, the log says
  why and the run carries on; a journal run (`--writes journal`) writes none.
- `[review]` and `after_run` are independent: use either, or both.

## Sharing settings with other tools: `base`

Several tools built on the shared library can keep their common settings in one file, named at the top of each
tool's settings: `base = "base-config.toml"` (relative to the settings file). Then:

- **the base's `[paths]` are variables** for this tool's paths (`log_file = "%EA_LOCAL%/calendar-team-leave-sync.log"`,
  `edge_profile = "%EDGE_PEOPLE_FIRST%"`), looked up before the environment;
- **its other settings that this tool knows and doesn't set itself are used**: typically `login_hint`, `tenant`,
  `msal_cache` and `time_zone` (the calendar entries' time zone when `[event]` doesn't set its own);
- **a table such as `[review]` applies only if this file has that section too** (even empty); its keys fill in
  the section, this file's own winning.

A missing or unreadable base is a settings error. The loader is background-tools-lib's `PyCompat.BaseSettings`.

## Settings

Everything else is optional:

```toml
base = "base-config.toml"   # optional: settings shared with other tools
login_hint = "you@example.com"
tenant = "organizations"          # or your tenant id
msal_cache = "..."                # share another tool's Microsoft sign-in
after_run = ["powershell", "-File", "hook.ps1"]
days_ahead = 365                  # how far ahead to copy leave; if unset, long_range_months_ahead (calendar months,
                                  # usually from the base file, shared with other tools), else 365 days
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
time_zone = "Europe/London"       # default: the top-level time_zone (from a base), else Europe/London

[names]
"Robert Example" = "Bob Example"

[approvals]                       # see "Leave approvals"; no section: off
act = false

[review]                          # see "Review queue"; no section: off
file = "%LOCALAPPDATA%/my-tools/review.json"
source = "calendar-team-leave-sync"
```

**Microsoft permissions:** `Calendars.ReadWrite` (delegated), signed in as Microsoft's own Graph
Command Line Tools app unless you set your own.

## Before sharing it

It reads People First with a token taken from the web app, in a hidden Edge window that closes as soon as it has it. That's fine for
your own use; **clear it with your HR system's owners and information security before others use it.**
A central alternative is People First's webhooks, set up by its administrator, feeding a small service
that writes to managers' calendars.

## Writes, and dry runs

**Every calendar write, and every People First approval, goes through one door** (`--writes live|journal|refuse`,
default live; `--journal FILE`).
A dry run always refuses, so a bug that tried to write would fail loudly rather than write. **Journal mode records
what a real sync would send, and sends nothing**, which is the way to watch it for a while before trusting it.
**A new entry is never sent twice:** if Graph might have created it (a timeout, a 5xx), it isn't resent, and the next
run sees what's really there. Nor is an approval. In journal mode an approval is recorded as
`POST people-first:/hrm/absences/approved?...`, and nothing about approvals is recorded or notified.

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/TeamLeave.Console -c Release -r win-x64 -o out
dotnet publish src/TeamLeave.Background -c Release -r win-x64 -o out
```

A tag `v*` builds and publishes a release.
