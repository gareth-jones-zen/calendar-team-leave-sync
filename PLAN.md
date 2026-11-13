# Plan: team-leave ported to .NET 11 Native AOT

*13 Nov 2026, written alongside the port (branch `dotnet`; the Python is tagged `v0.1.0`). Phases 1 to 3 are done, and
phase 4's People First half; its calendar half was deferred by Microsoft's outage. 5 and 6 wait for GJ.*

## The goal

**Every write must be byte for byte the Python version's**: the calendar requests (method, URL and the exact body
bytes), the state file, the report (`sync --json`), the summary line, the log lines, the notifications, the hook's
stdin and the exit codes. Same settings file, state file, log, task name (`team-leave sync`), run lock
(`team-leave-run-<key>`) and People First profile lock (`people-first-edge-<key>`, shared with org-directory and
cover-rota), so either version can replace the other with no data moved, and a Python and a .NET run exclude each
other.

## Names

| | Python | .NET |
|---|---|---|
| Command | `python -m team_leave` (pythonw for the task) | `team-leave.exe`; `team-leave-background.exe` (no window) for the task |
| Installed to | pip, editable from `~/source/repos/team-leave` | unzip to `%LOCALAPPDATA%\Programs\team-leave\` |
| Settings, state, log, task, locks | as set in `tools/team-leave.toml` | unchanged, the same files and names |

## Module map

| Python | .NET | Notes |
|---|---|---|
| `leave.py` | `Leave.cs` | PeopleFirstClient's `Session`; answers re-read as `json.loads` would give them (dict keys by Python equality) |
| `calendar.py` | `Calendar.cs`, `CaseFold.cs` | the hash is sha256 of `json.dumps(sort_keys=True)`; the hand-entry pattern spells out Python's `re.IGNORECASE` (generated from Python 3.14.5's Unicode data) |
| `sync.py` | `Sync.cs` | the plan, the safety stops, the report in Python's key order |
| `run.py` | `Run.cs` | lock, state, notifications, the hook through WinBackground's `Shell.Run` |
| `config.py`, `cli.py`, `autostart.py` | `Config.cs`, `Cli.cs`, `Autostart.cs` | Tomlyn; argparse's messages and exit codes; the task through WinBackground |
| (new) | `Graph.cs` | the `IGraph` seam, the read-only fence, the write door (Live, Journal, Refuse) and M365Graph underneath |
| (new) | `PyBits.cs` | Python behaviours not in PyCompat: `str.format` for plain fields, `repr`, `date.fromisoformat` as CPython's C reads it |

## How parity was checked

1. **The 37 Python tests, by name**, plus door, fence and misc tests (63; one is the local golden, skipped without it).
2. **The Python as an oracle** (`tests/parity/oracle.py`): the Python command line with every way out faked.
   `scenarios.py` makes synthetic scenarios (leave shapes, half days, owned entries current, stale, edited, timed,
   past or duplicated, hand-made entries exact or in pieces, adoption, the safety stops, failures of every kind,
   hours, the hook). 60 scenarios (141 steps) are committed; two local runs of 1,500 (seeds 7 and 13: 3,536 and
   3,539 steps) matched through the AOT exe. (The seed-13 run's first pass had one step fail and the next inherit its
   state; the case alone, and the whole run again, matched: a transient file clash on the laptop, not the port.)
3. **Native AOT:** both exes publish with no warnings; the AOT-compiled ParityRunner replays every golden.
4. **Live, read-only, on copies** (`tests/parity/live.py`, `tests/ParityRunner`), 13 Nov 2026:
   - **People First: byte-identical.** Each version read it on a copy of the Edge profile (copied under the
     `people-first-edge-…` lock): the same 6 calls, team listing and 4 event listings, the same file bytes.
   - **The calendar: deferred.** Microsoft's partial outage gave 502 and 504 on every calendar read from 07:40 to
     08:05, from both versions (and from the live Python tools); OneNote reads were fine.
   - **Still to do when Graph is back** (about ten minutes): copy the profile and cache again; `live.py
     record-graph` and `ParityRunner record-graph` on the copy, then `live.py compare`; `live.py build` on the
     recorded inputs and `ParityRunner replay` of the result; then `sync --dry-run --json` (and `--adopt`) from
     `python -m team_leave` and the AOT `team-leave.exe`, stdin redirected so neither can open a browser, compared
     with `live.py report`. Delete the copies afterwards.

## Phases

1. **Set-up (done):** fresh clone at `~/source/repos/team-leave-dotnet`, tag `v0.1.0`, branch `dotnet`.
2. **Oracle (done):** the scenario and misc goldens.
3. **Port (done):** module by module against the goldens; CI builds, tests and publishes both exes.
4. **Side-by-side checks (read-only):** People First done; the calendar half waits for Graph (above).
5. **Shadow (GJ):** a week of the .NET exe run by hand beside the Python task: `sync --dry-run --json` on a copy of
   the settings with its own state and log, comparing its report with the Python run's each morning. (Or
   `sync --writes journal`, which records what a real sync would send.)
6. **Switch (GJ):**
   1. `! python -m team_leave --config tools/team-leave.toml autostart off`;
   2. unzip the release to `%LOCALAPPDATA%\Programs\team-leave\`;
   3. `! "%LOCALAPPDATA%\Programs\team-leave\team-leave.exe" --config tools/team-leave.toml autostart on`;
   4. one EA commit: CLAUDE.md's "Team leave" section and the toml's comments (`python -m team_leave` becomes the
      exe);
   5. **rollback:** `team-leave autostart off`, then the Python `autostart on`. Nothing else moves.

## Decisions taken overnight (GJ's to overrule)

| # | Decision | Why |
|---|---|---|
| 1 | A create (POST) isn't resent once it may have reached Graph | Python's resend after a 5xx could make a duplicate entry; the next sync would delete it, but it's a visible blip in GJ's calendar |
| 2 | A write door and read-only fence, with `--writes` and `--journal` | As mail-triage: a dry run can't write even through a bug, and a shadow run can show what it would send |
| 3 | The run lock's key uses Python's lower-casing | So a .NET and a Python sync of the same settings exclude each other for every path |
| 4 | Sibling-checkout references to background-tools-lib | As the other ports |
| 5 | x64 only; the Python stays on the branch until the switch | As the other ports |

## Risks

- **People First's sign-in** is the fragile part, as in Python; the .NET client stops at once when a person is
  needed.
- **Leave is personal data:** every check used copies and compared by hash; nothing about anyone's leave is in the
  goldens, which are synthetic.
