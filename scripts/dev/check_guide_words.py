#!/usr/bin/env python3
"""The guide states; it does not sell. Fails on the words that do.

    python3 scripts/dev/check_guide_words.py
"""

from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
PAGES = sorted((ROOT / "docs" / "guide").glob("*.md")) + [ROOT / "README.md"]
BANNED = [
    "beginner", "friendly", "easy", "easily", "simply", "powerful", "seamless", "robust",
    "intuitive", "leverage", "empower", "benefit", "you should see", "let's", "step-by-step",
    "unlock", "supercharge", "effortless", "hassle",
]
PATTERN = re.compile(r"\b(" + "|".join(re.escape(w) for w in BANNED) + r")\b", re.I)


def main() -> int:
    problems = 0
    for page in PAGES:
        for number, line in enumerate(page.read_text().splitlines(), 1):
            for match in PATTERN.finditer(line):
                print(f"{page.relative_to(ROOT)}:{number}: {match.group(0)!r}")
                problems += 1
    print(f"{len(PAGES)} files, {problems} hits")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
