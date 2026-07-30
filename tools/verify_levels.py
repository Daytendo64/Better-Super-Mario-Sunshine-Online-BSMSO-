#!/usr/bin/env python3
"""Validate assets/levels.ntsc-u.json against RAScript course ID tables."""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEVELS_PATH = ROOT / "assets" / "levels.ntsc-u.json"

# Subset of timenoe/RAScripts courseIDs (NTSC-U) — every playable area the roster can show.
RASCRIPT_COURSE_IDS = {
    "Delfino Airstrip": 0x00,
    "Delfino Plaza": 0x01,
    "Bianco Hills": 0x02,
    "Ricco Harbor": 0x03,
    "Gelato Beach": 0x04,
    "Pinna Park - Beach": 0x05,
    "Sirena Beach - Beach": 0x06,
    "Sirena Beach - Hotel": 0x07,
    "Pianta Village": 0x08,
    "Noki Bay": 0x09,
    "Pinna Park - Park": 0x0D,
    "Sirena Beach - Casino": 0x0E,
    "Noki Bay - The Red Coin Fish": 0x10,
    "Delfino Plaza - Delfino Airstrip": 0x14,
    "Delfino Plaza - Super Slide": 0x15,
    "Delfino Plaza - Pachinko Game": 0x16,
    "Delfino Plaza - Red Coin Field": 0x17,
    "Delfino Plaza - Lily Pad Ride": 0x18,
    "Delfino Plaza - Turbo Track": 0x1D,
    "Ricco Harbor - Blooper Surfing Safari": 0x1E,
    "Noki Bay - The Shell's Secret": 0x1F,
    "Gelato Beach - Dune Bud Sand Castle Secret": 0x20,
    "Gelato Beach - The Sand Bird is Born": 0x21,
    "Sirena Beach - The Secret of Casino Delfino": 0x28,
    "Pinna Park - The Yoshi-Go-Round's Secret": 0x29,
    "Pianta Village - Secret of the Village Underside": 0x2A,
    "Noki Bay - Red Coins in a Bottle": 0x2C,
    "Bianco Hills - The Secret of the Dirty Lake": 0x2E,
    "Bianco Hills - The Hillside Cave Secret": 0x2F,
    "Ricco Harbor - The Secret of Ricco Tower": 0x30,
    "Pinna Park - The Beach Cannon's Secret": 0x32,
    "Sirena Beach - The Hotel Lobby's Secret": 0x33,
    "Corona Mountain": 0x34,
    "Bianco Hills - Down with Petey Piranha!": 0x37,
    "Sirena Beach - King Boo Down Below": 0x38,
    "Noki Bay - Eely-Mouth's Dentist": 0x39,
    "Pinna Park - Roller Coaster": 0x3A,
    "Ricco Harbor - Gooper Blooper Breaks Out": 0x3B,
    "Corona Mountain - Father and Son Shine!": 0x3C,
}

NAME_ALIASES = {
    "Pinna Park": "Pinna Park - Beach",
    "Pinna Park — Park Area": "Pinna Park - Park",
    "Sirena Beach": "Sirena Beach - Beach",
    "Sirena Beach — Hotel Interior": "Sirena Beach - Hotel",
    "Sirena Beach — King Boo Down Below": "Sirena Beach - King Boo Down Below",
    "Sirena Beach — Casino Delfino": "Sirena Beach - Casino",
    "Noki Bay — Undersea": "Noki Bay - The Red Coin Fish",
    "Bianco Hills — Hillside Cave Secret": "Bianco Hills - The Hillside Cave Secret",
    "Bianco Hills — Dirty Lake Secret": "Bianco Hills - The Secret of the Dirty Lake",
    "Ricco Harbor — Ricco Tower Secret": "Ricco Harbor - The Secret of Ricco Tower",
    "Pinna Park — Beach Cannon Secret": "Pinna Park - The Beach Cannon's Secret",
    "Sirena Beach — Hotel Lobby Secret": "Sirena Beach - The Hotel Lobby's Secret",
    "Sirena Beach — Casino Delfino Secret": "Sirena Beach - The Secret of Casino Delfino",
    "Pinna Park — Yoshi-Go-Round Secret": "Pinna Park - The Yoshi-Go-Round's Secret",
    "Pianta Village — Village Underside Secret": "Pianta Village - Secret of the Village Underside",
    "Noki Bay — Shell's Secret": "Noki Bay - The Shell's Secret",
    "Gelato Beach — Sand Castle Secret": "Gelato Beach - Dune Bud Sand Castle Secret",
    "Gelato Beach — The Sand Bird is Born": "Gelato Beach - The Sand Bird is Born",
    "Noki Bay — Red Coins in a Bottle": "Noki Bay - Red Coins in a Bottle",
    "Bianco Hills — Down with Petey Piranha!": "Bianco Hills - Down with Petey Piranha!",
    "Noki Bay — Eely-Mouth's Dentist": "Noki Bay - Eely-Mouth's Dentist",
    "Pinna Park — Roller Coaster": "Pinna Park - Roller Coaster",
    "Ricco Harbor — Gooper Blooper Breaks Out": "Ricco Harbor - Gooper Blooper Breaks Out",
    "Corona Mountain — Father and Son Shine!": "Corona Mountain - Father and Son Shine!",
    "Delfino Plaza — Delfino Airstrip (return)": "Delfino Plaza - Delfino Airstrip",
    "Delfino Plaza — Super Slide": "Delfino Plaza - Super Slide",
    "Delfino Plaza — Pachinko Game": "Delfino Plaza - Pachinko Game",
    "Delfino Plaza — Red Coin Field": "Delfino Plaza - Red Coin Field",
    "Delfino Plaza — Lily Pad Ride": "Delfino Plaza - Lily Pad Ride",
    "Delfino Plaza — Turbo Track": "Delfino Plaza - Turbo Track",
    "Ricco Harbor — Blooper Surfing Safari": "Ricco Harbor - Blooper Surfing Safari",
}

# Every RAScript playable area (except title/test) must appear in the catalog.
REQUIRED_COURSE_IDS = sorted(
    {cid for name, cid in RASCRIPT_COURSE_IDS.items() if name != "Title Screen/Credits"}
)


def main() -> int:
    if not LEVELS_PATH.exists():
        print(f"ERROR: missing {LEVELS_PATH}")
        return 1

    data = json.loads(LEVELS_PATH.read_text(encoding="utf-8"))
    errors = []
    seen_ids = set()

    for course in data.get("courses", []):
        cid = course["courseId"]
        name = course["displayName"]
        seen_ids.add(cid)
        alias = NAME_ALIASES.get(name, name)
        if alias in RASCRIPT_COURSE_IDS:
            expected = RASCRIPT_COURSE_IDS[alias]
            if cid != expected:
                errors.append(f"{name}: courseId {cid} != RAScript {expected}")
        if not course.get("episodes"):
            errors.append(f"{name}: no episodes defined")
        for ep in course.get("episodes", []):
            if ep["episodeId"] > 7 and course["warpable"]:
                pass  # hub/special stages may use 0 only

    for required in REQUIRED_COURSE_IDS:
        if required not in seen_ids:
            errors.append(f"missing courseId {required} (RAScript playable area)")

    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        return 1

    print(f"OK: {len(data['courses'])} courses validated "
          f"({len(REQUIRED_COURSE_IDS)} RAScript playable ids covered)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
