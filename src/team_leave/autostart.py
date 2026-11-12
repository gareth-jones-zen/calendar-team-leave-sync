"""A repeating sync through Windows Task Scheduler (win-background).

The task runs `sync --hook --scheduled` every every_minutes, and at logon, and catches up a run missed
while the laptop was asleep. A scheduled run outside `hours` (and at weekends, with `weekdays`) exits at
once, writing nothing and logging nothing. A run with nothing to change makes only the reads. It runs as
you, only while you're logged on, with no admin rights needed, and runs pythonw directly, so there's no
window.
"""

from __future__ import annotations

import datetime as dt
import pathlib

from win_background import Task, python_command

TASK = "team-leave sync"
_task = Task(TASK, "Copies your reports' approved leave from People First into your calendar (team-leave sync).")


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("team_leave", "--config", str(config_path.resolve()), "sync", "--hook", "--scheduled")


def enable(config_path: pathlib.Path, every_minutes: int) -> str:
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent, at_logon=True,
                        every=dt.timedelta(minutes=every_minutes), keep_running=False,
                        time_limit=dt.timedelta(minutes=20))


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
