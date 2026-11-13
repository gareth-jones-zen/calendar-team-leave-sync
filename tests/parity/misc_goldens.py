"""Small goldens from the Python version, for the pieces the scenarios reach only now and then:
hand_pattern() on awkward names and subjects, date.fromisoformat() on odd strings, digest() on payloads with
awkward text and numbers, and urllib.parse.quote() on the two Graph query strings.

    python tests/parity/misc_goldens.py tests/TeamLeave.Tests/Golden/misc.json
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import random
import re
import sys
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

from team_leave import calendar as cal  # noqa: E402

NAMES = ["Jordan Lee", "Zoë Ng", "İlker Ünal", "Anne-Marie O'Brien", "J. (Jo) Smith", "a+b c", "Q$ [x]", "Σοφία ΣΑΣ", "ſam K",
         "Kelvin Lee", "Ölçer Đoković", "Mo #1", "Tab\tName", "Max ~ Min", "x|y", "Dan Clark", "Ægir Sarwar"]
TAILS = ["annual leave", "on leave", "Annual Leave", "ON LEAVE", "annual  leave", "annual leave (AM)", "annual leave PM",
         "annual leave (from PM)", "on leave (to AM)", "annual leave (from PM) (to AM)", "annual leave\n", "annual leave\n\n",
         "annual leave - skiing", "annual leave am pm am", "annual leave (am", "annual leave pm)", "annual leave ( from  pm )",
         "annual leave ", "on leave (AM)", "annual leave (to AM)x", "sick", "annual leaves", "annual leave from am",
         "annual leave to pm (am)"]


def main() -> None:
    r = random.Random(5)
    patterns = []
    for name in NAMES:
        p = cal.hand_pattern(name)
        variants = {name, name.lower(), name.upper(), " " + name, name + "s", name.replace(" ", "  ")}
        for v in sorted(variants):
            for tail in TAILS:
                for sep in (" ", "  ", "\t", ""):
                    subject = f"{v}{sep}{tail}"
                    patterns.append({"name": name, "subject": subject, "match": bool(p.match(subject))})
    dates = []
    for text in ["2026-12-24", "20261224", "2026-W52-4", "2026W524", "2026-W52", "2026W52", "2026-12-2", "2026-1224", "202612-24",
                 "2026-13-01", "2026-02-29", "2028-02-29", "0000-01-01", "9999-12-31", "2026-W53-1", "2020-W53-7", "None", "",
                 "2026-12-24T", "2026122412", "20261224ab", "2026W524", "2026-W52-", "2026-W5", "2026-W52X", "2026-12-2X", "202612-24", "2026W53", "2020W53", "2026W00", "é026-12-2", "2026-éé-1", "2026\n12\n24", "２０２６-12-24", "2026-12-24 ", " 2026-12-2", "2026-W00-1", "2026-W01-8", "abcdefghij"]:
        try:
            dates.append({"text": text, "date": dt.date.fromisoformat(text).isoformat()})
        except ValueError:
            dates.append({"text": text, "date": None})
    digests = []
    words = ["Zoë", "annual leave", " ", "😀", "\"quoted\"", "back\\slash", "tab\t", "café", "", "Planned"]
    for _ in range(300):
        payload = {"subject": " ".join(r.choice(words) for _ in range(r.randint(0, 4))),
                   "start": {"dateTime": "2026-12-24T00:00:00", "timeZone": r.choice(["Europe/London", "UTC"])},
                   "isAllDay": r.choice([True, False]), "categories": [r.choice(words) for _ in range(r.randint(0, 3))]}
        if r.random() < 0.5:
            payload["reminderMinutesBeforeStart"] = r.choice([0, 1080, -5, 2 ** 40])
        if r.random() < 0.2:
            payload["categories"].append(r.choice([3, 2.5, 1e20, -0.0]))
        digests.append({"payload": json.dumps(payload), "digest": cal.digest(payload)})
    quotes = [{"text": t, "quoted": urllib.parse.quote(t)} for t in [
        f"singleValueExtendedProperties/Any(ep: ep/id eq '{cal.ABSENCE_PROP}' and ep/value ne null)",
        f"singleValueExtendedProperties($filter=id eq '{cal.ABSENCE_PROP}' or id eq '{cal.HASH_PROP}')",
        "a b~c_d.e-f/g", "Zoë é 😀 %"]]
    # Python's re, ignoring case, for every cased code point in a few blocks (Latin, Greek, Cyrillic, Armenian,
    # Georgian, Cherokee, letterlike symbols, Glagolitic, Deseret): every code point it matches, by asking re itself
    import _sre
    blocks = [range(0x0, 0x590), range(0x10A0, 0x1100), range(0x13A0, 0x1400), range(0x1C80, 0x1CC0), range(0x1E00, 0x2000),
              range(0x2100, 0x2190), range(0x2C00, 0x2D30), range(0xA640, 0xA800), range(0xFB00, 0xFB10), range(0xFF20, 0xFF60),
              range(0x10400, 0x10450)]
    candidates = [d for b in blocks for d in b]
    folds = []
    for c in candidates:
        if not _sre.unicode_iscased(c):
            continue
        p = re.compile(re.escape(chr(c)), re.I)
        folds.append([c, [d for d in candidates if p.fullmatch(chr(d))]])
    out = {"patterns": patterns, "dates": dates, "digests": digests, "quotes": quotes, "folds": folds}
    pathlib.Path(sys.argv[1]).write_text(json.dumps(out, ensure_ascii=False), encoding="utf-8")
    print(f"{len(patterns)} patterns, {len(dates)} dates, {len(digests)} digests, {len(quotes)} quotes, {len(folds)} folds")


if __name__ == "__main__":
    main()
