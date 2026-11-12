"""Run with: python -m unittest discover -s tests   (no People First, no Graph: fakes for both)

"Today" is Thu 12 Nov 2026. Alex manages Jordan Lee and Robin Hall. People First's listings are built by
listing(); the calendar is FakeCalendar, which records every write.
"""

import datetime as dt
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from team_leave import calendar as cal, config as cfg, leave, run as runner, sync  # noqa: E402

TODAY = dt.date(2026, 11, 12)
D = dt.date.fromisoformat
TYPES = [{"id": "T1", "value": "Holiday"}, {"id": "T2", "value": "My Birthday"}, {"id": "T3", "value": "Sickness"},
         {"id": "T4", "value": "Appointment"}]
PARTS = [{"id": "FULL", "value": "All day"}, {"id": "HALF_AM", "value": "AM"}, {"id": "HALF_PM", "value": "PM"}]
SETTINGS = """
login_hint = "alex@example.com"
max_deletes = 3
[people_first]
url = "https://example.people-first.com"
[types]
"Holiday" = "{name} annual leave"
"My Birthday" = "{name} annual leave"
"""
JORDAN, ROBIN = leave.Person("P1", "Jordan Lee"), leave.Person("P2", "Robin Hall")


def config(text=SETTINGS, **override):
    folder = pathlib.Path(tempfile.mkdtemp())
    path = folder / "config.toml"
    path.write_text(text, encoding="utf-8")
    c = cfg.load(path)
    c.state_file = folder / "state.json"
    c.log_file = folder / "log.txt"
    for k, v in override.items():
        setattr(c, k, v)
    return c


def event(eid, start, end, type_id="T1", state="Approved", start_part="FULL", end_part="FULL"):
    return {"eventId": eid, "personId": "P", "startDate": start, "endDate": end, "eventTypeId": type_id,
            "state": state, "startTypeId": start_part, "endTypeId": end_part, "absenceDurationUnit": "Daily"}


def listing(*events):
    return {"data": {"events": list(events)},
            "meta": {"events.eventTypeId": {"values": TYPES}, "events.startTypeId": {"values": PARTS},
                     "events.endTypeId": {"values": PARTS}}}


def absence(c, eid, person, start, end, sp="FULL", ep="FULL"):
    return leave.Absence(eid, person, D(start), D(end), sp, ep, c.types["Holiday"])


class FakeCalendar:
    def __init__(self, entries=()):
        self.entries = {e.id: e for e in entries}
        self.writes = []
        self.between_calls = 0

    def owned(self):
        return [e for e in self.entries.values() if e.absence]

    def between(self, first, last):
        self.between_calls += 1
        return [e for e in self.entries.values() if e.end >= first and e.start <= last]

    def create(self, payload):
        self.writes.append(("create", payload))
        return "new"

    def update(self, event_id, payload):
        self.writes.append(("update", event_id, payload))

    def delete(self, event_id):
        self.writes.append(("delete", event_id))


def owned_entry(c, a, eid="E1", **changes):
    """The entry team-leave would have written for absence a."""
    payload = cal.body(c, a)
    e = cal.Entry(eid, payload["subject"], a.start, a.end, True, absence=a.id, hash=cal.digest(payload))
    for k, v in changes.items():
        setattr(e, k, v)
    return e


def hand(eid, subject, start, end, **extra):
    return cal.Entry(eid, subject, D(start), D(end), extra.pop("all_day", True), **extra)


class ConfigTest(unittest.TestCase):
    def test_defaults(self):
        c = config("[people_first]\nurl = 'https://acme.people-first.com'\n")
        self.assertEqual(c.types, {"Holiday": "{name} annual leave"})
        self.assertEqual(c.states, ["Approved"])
        self.assertEqual(c.people_first.tenant_code, "acme")
        self.assertEqual((c.event.categories, c.event.reminder, c.event.sensitivity, c.event.show_as),
                         (["Planned"], None, "normal", "free"))
        self.assertEqual((c.days_ahead, c.every_minutes, c.hours, c.weekdays), (365, 15, ("07:00", "19:00"), True))

    def test_bad_settings(self):
        for text in ("[people_first]\nurl='http://x'\n", SETTINGS + "[titles]\nam = '(AM)'\n",
                     SETTINGS.replace('"{name} annual leave"\n"My', '"annual leave"\n"My'),
                     SETTINGS + "[event]\nshow_as = 'away'\n", SETTINGS + "[event]\nreminder = true\n"):
            with self.assertRaises(ValueError, msg=text):
                config(text)


class LeaveTest(unittest.TestCase):
    def test_types_and_states(self):
        c = config()
        body = listing(event("A1", "2026-12-24", "2026-12-31"), event("A2", "2027-03-03", "2027-03-03", "T2"),
                       event("A3", "2026-11-20", "2026-11-20", "T3"), event("A4", "2026-11-25", "2026-11-25", "T4"),
                       event("A5", "2027-01-05", "2027-01-06", state="AwaitingApproval"),
                       event("A6", "2027-01-07", "2027-01-07", state="Rejected"))
        found = leave.absences(c, JORDAN, body)
        self.assertEqual([a.id for a in found], ["A1", "A2"])
        # The birthday looks exactly like annual leave: the type never reaches the title.
        self.assertEqual([a.title(c) for a in found], ["Jordan Lee annual leave", "Jordan Lee annual leave"])

    def test_holiday_only_by_default(self):
        c = config("[people_first]\nurl = 'https://acme.people-first.com'\n")
        found = leave.absences(c, JORDAN, listing(event("A1", "2026-12-24", "2026-12-24"),
                                                  event("A2", "2027-03-03", "2027-03-03", "T2")))
        self.assertEqual([a.id for a in found], ["A1"])

    def test_half_day_titles(self):
        c = config()
        cases = [("2026-12-01", "2026-12-01", "HALF_AM", "HALF_AM", "Jordan Lee annual leave (AM)"),
                 ("2026-12-01", "2026-12-01", "HALF_PM", "HALF_PM", "Jordan Lee annual leave (PM)"),
                 ("2026-12-01", "2026-12-01", "FULL", "HALF_PM", "Jordan Lee annual leave (PM)"),
                 ("2026-12-01", "2026-12-01", "HALF_AM", "HALF_PM", "Jordan Lee annual leave"),
                 ("2026-12-01", "2026-12-03", "HALF_PM", "FULL", "Jordan Lee annual leave (from PM)"),
                 ("2026-12-01", "2026-12-03", "FULL", "HALF_AM", "Jordan Lee annual leave (to AM)"),
                 ("2026-12-01", "2026-12-03", "HALF_PM", "HALF_AM", "Jordan Lee annual leave (from PM) (to AM)"),
                 ("2026-12-01", "2026-12-03", "HALF_AM", "HALF_PM", "Jordan Lee annual leave")]
        for start, end, sp, ep, title in cases:
            self.assertEqual(absence(c, "A", JORDAN, start, end, sp, ep).title(c), title)

    def test_parts_by_annotated_name(self):
        c = config()
        body = listing(event("A1", "2026-12-01", "2026-12-01", start_part="X9", end_part="X9"))
        body["meta"]["events.startTypeId"]["values"].append({"id": "X9", "value": "PM"})
        body["meta"]["events.endTypeId"]["values"].append({"id": "X9", "value": "PM"})
        self.assertEqual(leave.absences(c, JORDAN, body)[0].title(c), "Jordan Lee annual leave (PM)")

    def test_names_override(self):
        c = config(SETTINGS + '[names]\n"Robert Hall" = "Robin Hall"\n')
        self.assertEqual(leave.display_name(c, {"firstName": "Robert", "lastName": "Hall"}), "Robin Hall")
        self.assertEqual(leave.display_name(c, {"firstName": "Jordan", "lastName": "Lee"}), "Jordan Lee")

    def test_changed_shape_raises(self):
        c = config()
        with self.assertRaises(leave.LeaveError):
            leave.absences(c, JORDAN, {"data": {"events": []}, "meta": {}})
        with self.assertRaises(leave.LeaveError):
            leave.absences(c, JORDAN, {"items": []})

    def test_unknown_configured_type_raises(self):
        c = config(SETTINGS + '"Birthday Leave" = "{name} annual leave"\n')

        class Api:
            def me(self):
                return "ME"

            def team_members(self, person):
                return [{"personId": "P1", "firstName": "Jordan", "lastName": "Lee"}] if person == "ME" else []

            def events(self, person, start, end):
                return listing()

        with self.assertRaises(leave.LeaveError):
            leave.read(c, Api(), TODAY, log=lambda _: None)


class PlanTest(unittest.TestCase):
    def setUp(self):
        self.c = config()
        self.a1 = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        self.a2 = absence(self.c, "A2", ROBIN, "2026-11-20", "2026-11-20", "HALF_PM", "HALF_PM")

    def plan(self, absences, calendar, **kw):
        return sync.plan(self.c, [JORDAN, ROBIN], absences, calendar, TODAY, **kw)

    def test_create_update_delete_and_unchanged(self):
        moved = absence(self.c, "A1", JORDAN, "2026-12-23", "2026-12-31")
        gone = owned_entry(self.c, absence(self.c, "A9", ROBIN, "2026-12-01", "2026-12-01"), "E9")
        calendar = FakeCalendar([owned_entry(self.c, self.a1, "E1"), gone])
        p = self.plan([moved, self.a2], calendar)
        self.assertEqual([a.id for a, _ in p.create], ["A2"])
        self.assertEqual([(a.id, e.id) for a, _, e in p.update], [("A1", "E1")])
        self.assertEqual([e.id for e in p.delete], ["E9"])
        sync.apply(p, calendar, log=lambda _: None)
        kinds = [w[0] for w in calendar.writes]
        self.assertEqual(kinds, ["create", "update", "delete"])
        created = calendar.writes[0][1]
        self.assertEqual(created["subject"], "Robin Hall annual leave (PM)")
        self.assertEqual((created["start"]["dateTime"], created["end"]["dateTime"]),
                         ("2026-11-20T00:00:00", "2026-11-21T00:00:00"))
        self.assertTrue(created["isAllDay"])
        self.assertEqual((created["showAs"], created["sensitivity"], created["categories"], created["isReminderOn"]),
                         ("free", "normal", ["Planned"], False))
        props = {p["id"]: p["value"] for p in created["singleValueExtendedProperties"]}
        self.assertEqual(props[cal.ABSENCE_PROP], "A2")
        self.assertNotIn("Holiday", str(created))

    def test_nothing_to_do_writes_nothing_and_reads_no_calendar_range(self):
        calendar = FakeCalendar([owned_entry(self.c, self.a1, "E1"), owned_entry(self.c, self.a2, "E2")])
        p = self.plan([self.a1, self.a2], calendar)
        self.assertEqual((p.writes, p.unchanged, calendar.between_calls), (0, 2, 0))

    def test_hand_edit_is_overwritten(self):
        calendar = FakeCalendar([owned_entry(self.c, self.a1, "E1", subject="Jordan off")])
        p = self.plan([self.a1], calendar)
        self.assertEqual([e.id for _, _, e in p.update], ["E1"])

    def test_settings_change_rewrites(self):
        calendar = FakeCalendar([owned_entry(self.c, self.a1, "E1")])
        self.c.event.categories = ["Leave"]
        self.assertEqual(len(self.plan([self.a1], calendar).update), 1)

    def test_past_and_unowned_entries_never_touched(self):
        past = owned_entry(self.c, absence(self.c, "A8", JORDAN, "2026-11-02", "2026-11-06"), "E8")
        others = [hand("H1", "Team offsite", "2026-12-01", "2026-12-01"),
                  hand("H2", "Jordan Lee annual leave", "2026-10-01", "2026-10-02"),
                  hand("H3", "Robin Hall on leave", "2027-02-01", "2027-02-01")]
        calendar = FakeCalendar([past, *others])
        p = self.plan([self.a1], calendar, survey=True)
        sync.apply(p, calendar, log=lambda _: None)
        touched = {w[1] for w in calendar.writes if w[0] != "create"}
        self.assertEqual(touched, set())
        self.assertEqual([w[0] for w in calendar.writes], ["create"])
        # H3 looks like Robin's leave but matches no absence: reported, never touched.
        self.assertEqual([e.id for e in p.unmatched_hand], ["H3"])

    def test_ongoing_owned_entry_can_still_be_removed(self):
        ongoing = owned_entry(self.c, absence(self.c, "A7", JORDAN, "2026-11-10", "2026-11-13"), "E7")
        p = self.plan([self.a1], FakeCalendar([ongoing]))
        self.assertEqual([e.id for e in p.delete], ["E7"])

    def test_duplicate_owned_entries_tidied(self):
        calendar = FakeCalendar([owned_entry(self.c, self.a1, "E1"), owned_entry(self.c, self.a1, "E2")])
        p = self.plan([self.a1], calendar)
        self.assertEqual(([e.id for e in p.delete], p.unchanged), (["E2"], 1))


class AdoptTest(unittest.TestCase):
    def setUp(self):
        self.c = config()
        self.a1 = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        self.a2 = absence(self.c, "A2", ROBIN, "2026-11-20", "2026-11-20", "HALF_PM", "HALF_PM")

    def plan(self, absences, calendar, adopt=False):
        return sync.plan(self.c, [JORDAN, ROBIN], absences, calendar, TODAY, adopt=adopt)

    def test_exact_match_suppresses_create(self):
        calendar = FakeCalendar([hand("H1", "Jordan Lee on leave", "2026-12-24", "2026-12-31"),
                                 hand("H2", "robin hall annual leave PM", "2026-11-20", "2026-11-20")])
        p = self.plan([self.a1, self.a2], calendar)
        self.assertEqual((p.create, p.adopt), ([], []))
        self.assertEqual([[e.id for e in es] for _, es in p.by_hand], [["H1"], ["H2"]])
        sync.apply(p, calendar, log=lambda _: None)
        self.assertEqual(calendar.writes, [])

    def test_adopt_takes_over_and_retitles(self):
        calendar = FakeCalendar([hand("H1", "Jordan Lee on leave", "2026-12-24", "2026-12-31")])
        p = self.plan([self.a1], calendar, adopt=True)
        self.assertEqual([e.id for _, _, e in p.adopt], ["H1"])
        sync.apply(p, calendar, log=lambda _: None)
        op, event_id, payload = calendar.writes[0]
        self.assertEqual((op, event_id, payload["subject"]), ("update", "H1", "Jordan Lee annual leave"))
        self.assertIn(cal.ABSENCE_PROP, [x["id"] for x in payload["singleValueExtendedProperties"]])
        r = sync.report(self.c, p, dry_run=False)
        self.assertEqual(r["adopted"][0]["was"], "Jordan Lee on leave 24/12 to 31/12")

    def test_no_match_on_different_dates_other_name_attendees_or_timed(self):
        calendar = FakeCalendar([hand("H1", "Jordan Lee annual leave", "2026-12-24", "2026-12-30"),
                                 hand("H2", "Jordan Leeds annual leave", "2026-12-24", "2026-12-31"),
                                 hand("H3", "Jordan Lee annual leave", "2026-12-24", "2026-12-31", attendees=2),
                                 hand("H4", "Jordan Lee annual leave", "2026-12-24", "2026-12-31", all_day=False),
                                 hand("H5", "Jordan Lee annual leave - skiing", "2026-12-24", "2026-12-31"),
                                 hand("H6", "Jordan Lee annual leave", "2026-12-24", "2026-12-31", organiser=False)])
        p = self.plan([self.a1], calendar, adopt=True)
        self.assertEqual(([a.id for a, _ in p.create], p.adopt, p.by_hand), (["A1"], [], []))

    def test_ongoing_hand_entry_not_adopted(self):
        a = absence(self.c, "A3", JORDAN, "2026-11-10", "2026-11-13")
        calendar = FakeCalendar([hand("H1", "Jordan Lee annual leave", "2026-11-10", "2026-11-13")])
        p = self.plan([a], calendar, adopt=True)
        self.assertEqual((p.adopt, [[e.id for e in es] for _, es in p.by_hand]), ([], [["H1"]]))

    def test_split_by_hand_around_a_weekend(self):
        a = absence(self.c, "A3", JORDAN, "2026-11-18", "2026-11-25")      # Wed to the next Wed
        pieces = [hand("H1", "Jordan Lee annual leave", "2026-11-18", "2026-11-20"),
                  hand("H2", "Jordan Lee annual leave", "2026-11-23", "2026-11-25")]
        p = self.plan([a], FakeCalendar(pieces), adopt=True)
        self.assertEqual((p.create, p.adopt, [[e.id for e in es] for _, es in p.by_hand]), ([], [], [["H1", "H2"]]))
        r = sync.report(self.c, p, dry_run=True)
        self.assertEqual(r["by_hand"][0]["pieces"], 2)
        # A weekday not covered (Mon 23 Nov missing): not leave entered by hand, so team-leave writes its own.
        gap = [pieces[0], hand("H2", "Jordan Lee annual leave", "2026-11-24", "2026-11-25")]
        self.assertEqual([x.id for x, _ in self.plan([a], FakeCalendar(gap)).create], ["A3"])


class SafetyTest(unittest.TestCase):
    def setUp(self):
        self.c = config()

    def test_everything_vanished(self):
        a = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        calendar = FakeCalendar([owned_entry(self.c, a, "E1")])
        with self.assertRaises(sync.SafetyStop):
            sync.plan(self.c, [JORDAN], [], calendar, TODAY)
        # ...but with only past entries owned, an empty People First is fine.
        past = owned_entry(self.c, absence(self.c, "A2", JORDAN, "2026-10-01", "2026-10-02"), "E2")
        self.assertEqual(sync.plan(self.c, [JORDAN], [], FakeCalendar([past]), TODAY).writes, 0)

    def test_deletion_cap(self):
        keep = absence(self.c, "K", JORDAN, "2027-01-04", "2027-01-04")
        entries = [owned_entry(self.c, keep, "EK")] + [
            owned_entry(self.c, absence(self.c, f"A{i}", ROBIN, f"2026-12-0{i}", f"2026-12-0{i}"), f"E{i}")
            for i in range(1, 5)]
        with self.assertRaises(sync.SafetyStop):
            sync.plan(self.c, [JORDAN, ROBIN], [keep], FakeCalendar(entries), TODAY)
        p = sync.plan(self.c, [JORDAN, ROBIN], [keep], FakeCalendar(entries), TODAY, max_deletes=4)
        self.assertEqual(len(p.delete), 4)

    def test_vanished_can_be_overridden_explicitly(self):
        a = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        p = sync.plan(self.c, [JORDAN], [], FakeCalendar([owned_entry(self.c, a, "E1")]), TODAY, max_deletes=1)
        self.assertEqual([e.id for e in p.delete], ["E1"])

    def test_failed_read_writes_nothing(self):
        a = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        calendar = FakeCalendar([owned_entry(self.c, a, "E1")])

        def read(config, today, log):
            raise leave.LeaveError("People First answered 500 to /hrm/people/P2/events")

        with self.assertRaises(leave.LeaveError):
            runner.sync_once(self.c, today=TODAY, log=lambda _: None, graph=object(), read=read)
        self.assertEqual(calendar.writes, [])

    def test_safety_stop_in_a_run_writes_nothing(self):
        a = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        calendar = FakeCalendar([owned_entry(self.c, a, "E1")])
        original = runner.cal.Calendar
        runner.cal.Calendar = lambda graph, config: calendar
        try:
            with self.assertRaises(sync.SafetyStop):
                runner.sync_once(self.c, today=TODAY, log=lambda _: None, graph=object(),
                                 read=lambda config, today, log: ([JORDAN], []))
        finally:
            runner.cal.Calendar = original
        self.assertEqual(calendar.writes, [])


class RunTest(unittest.TestCase):
    """Hours gating, failure counting and notifications, through run.run with fakes."""

    def setUp(self):
        self.c = config()
        self.alerts = []
        self.calendar = FakeCalendar()
        self.original = runner.cal.Calendar
        runner.cal.Calendar = lambda graph, config: self.calendar

    def tearDown(self):
        runner.cal.Calendar = self.original

    def go(self, read, scheduled=True, **kw):
        return runner.run(self.c, scheduled=scheduled, log=lambda _: None, graph=object(), read=read, today=TODAY,
                          alert=lambda title, lines: self.alerts.append(title), **kw)

    def test_hours(self):
        c = self.c
        self.assertTrue(runner.in_hours(c, dt.datetime(2026, 11, 12, 7, 0)))
        self.assertFalse(runner.in_hours(c, dt.datetime(2026, 11, 12, 6, 59)))
        self.assertFalse(runner.in_hours(c, dt.datetime(2026, 11, 12, 19, 0)))
        self.assertFalse(runner.in_hours(c, dt.datetime(2026, 11, 14, 10, 0)))   # a Saturday
        c.weekdays = False
        self.assertTrue(runner.in_hours(c, dt.datetime(2026, 11, 14, 10, 0)))

    def test_outside_hours_does_nothing(self):
        called = []
        original = runner.in_hours
        runner.in_hours = lambda config, now: False
        try:
            self.assertIsNone(self.go(lambda *a: called.append(1)))
        finally:
            runner.in_hours = original
        self.assertEqual(called, [])

    def test_failures_notify_on_the_third_and_once(self):
        def read(config, today, log):
            raise leave.LeaveError("People First answered 503 to /hrm/people/P1/events")

        original = runner.in_hours
        runner.in_hours = lambda config, now: True
        try:
            for _ in range(5):
                with self.assertRaises(leave.LeaveError):
                    self.go(read)
            self.assertEqual(len(self.alerts), 1)
            self.assertEqual(runner.load_state(self.c)["failures"], 5)
            self.go(lambda config, today, log: ([JORDAN], []))   # a success clears it
            self.assertEqual(runner.load_state(self.c)["failures"], 0)
        finally:
            runner.in_hours = original

    def test_sign_in_notifies_at_once(self):
        import people_first

        def read(config, today, log):
            raise people_first.SignInNeeded("People First's sign-in wants a password.")

        original = runner.in_hours
        runner.in_hours = lambda config, now: True
        try:
            with self.assertRaises(people_first.SignInNeeded):
                self.go(read)
            self.assertEqual(self.alerts, ["team-leave needs a sign-in"])
        finally:
            runner.in_hours = original

    def test_state_holds_no_names_or_dates(self):
        a = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        self.go(lambda config, today, log: ([JORDAN], [a]), scheduled=False)
        text = self.c.state_file.read_text(encoding="utf-8")
        log = self.c.log_file.read_text(encoding="utf-8") if self.c.log_file.exists() else ""
        for secret in ("Jordan", "2026-12-24", "Holiday"):
            self.assertNotIn(secret, text)
        self.assertEqual([w[0] for w in self.calendar.writes], ["create"])
        self.assertEqual(log, "")   # the test's log function writes nowhere; see test_log_is_names_free

    def test_log_is_names_free(self):
        lines = []
        a = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        runner.run(self.c, log=lines.append, graph=object(), read=lambda config, today, log: ([JORDAN], [a]),
                   today=TODAY)
        joined = "\n".join(lines)
        for secret in ("Jordan", "24/12", "2026-12", "Holiday"):
            self.assertNotIn(secret, joined)

    def test_dry_run_writes_nothing(self):
        a = absence(self.c, "A1", JORDAN, "2026-12-24", "2026-12-31")
        r = self.go(lambda config, today, log: ([JORDAN], [a]), scheduled=False, dry_run=True)
        self.assertEqual((self.calendar.writes, r["added"][0]["text"]), ([], "Jordan Lee annual leave 24/12 to 31/12"))
        self.assertFalse(self.c.state_file.exists())


class CalendarTest(unittest.TestCase):
    """The Graph side: reading entries back, against a fake m365_graph client."""

    def test_entry_dates_and_properties(self):
        item = {"id": "X", "subject": "Jordan Lee annual leave", "isAllDay": True, "isOrganizer": True,
                "start": {"dateTime": "2026-12-24T00:00:00.0000000"}, "end": {"dateTime": "2027-01-01T00:00:00.0000000"},
                "singleValueExtendedProperties": [{"id": cal.ABSENCE_PROP, "value": "A1"},
                                                  {"id": cal.HASH_PROP, "value": "abc"}]}
        e = cal.entry(item)
        self.assertEqual((e.start, e.end, e.absence, e.hash), (D("2026-12-24"), D("2026-12-31"), "A1", "abc"))

    def test_owned_query_and_create(self):
        class Graph:
            def __init__(self):
                self.calls = []

            def pages(self, url, scopes=None, headers=None):
                self.calls.append(("GET", url, headers))
                return iter([])

            def request(self, url, method="GET", body=None, scopes=None, headers=None):
                self.calls.append((method, url, body))
                return {"id": "new"}

        c = config()
        g = Graph()
        calendar = cal.Calendar(g, c)
        self.assertEqual(calendar.owned(), [])
        self.assertIn("singleValueExtendedProperties/Any", g.calls[0][1])
        self.assertEqual(g.calls[0][2]["Prefer"], 'outlook.timezone="Europe/London"')
        calendar.create({"subject": "x"})
        self.assertEqual(g.calls[1][2]["attendees"], [])

    def test_hand_pattern(self):
        p = cal.hand_pattern("Jordan Lee")
        for ok in ("Jordan Lee annual leave", "jordan lee on leave", "Jordan Lee annual leave (AM)",
                   "Jordan Lee annual leave PM", "Jordan Lee annual leave (from PM)", "Jordan Lee on leave (to AM)",
                   "Jordan Lee annual leave (from PM) (to AM)"):
            self.assertTrue(p.match(ok), ok)
        for bad in ("Jordan Leeds annual leave", "Jordan Lee annual leave - skiing", "Re: Jordan Lee annual leave",
                    "Jordan Lee sick"):
            self.assertFalse(p.match(bad), bad)

    def test_reminder_minutes(self):
        c = config(SETTINGS + "[event]\nreminder = 1080\nsensitivity = 'private'\n")
        b = cal.body(c, absence(c, "A1", JORDAN, "2026-12-24", "2026-12-24"))
        self.assertEqual((b["isReminderOn"], b["reminderMinutesBeforeStart"], b["sensitivity"]), (True, 1080, "private"))


if __name__ == "__main__":
    unittest.main()
