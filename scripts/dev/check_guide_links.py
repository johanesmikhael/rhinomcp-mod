#!/usr/bin/env python3
"""Every link and image in the guide and the root README resolves; every image is used.

    python3 scripts/dev/check_guide_links.py
"""

from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
GUIDE = ROOT / "docs" / "guide"
IMG = GUIDE / "img"
LINK = re.compile(r"!?\[[^\]]*\]\(([^)\s]+)\)")


def main() -> int:
    pages = sorted(GUIDE.glob("*.md")) + [ROOT / "README.md", ROOT / "docs" / "dev" / "README.md"]
    problems: list[str] = []
    referenced: set[pathlib.Path] = set()
    for page in pages:
        for match in LINK.finditer(page.read_text()):
            target = match.group(1)
            if target.startswith(("http://", "https://", "mailto:")):
                continue
            path_part, _, anchor = target.partition("#")
            if not path_part:
                continue  # same-page anchor; headings are not checked
            resolved = (page.parent / path_part).resolve()
            if not resolved.exists():
                problems.append(f"{page.relative_to(ROOT)}: {target} -> {resolved.relative_to(ROOT) if resolved.is_relative_to(ROOT) else resolved} missing")
            elif resolved.suffix.lower() == ".png":
                referenced.add(resolved)

    for image in sorted(IMG.glob("*.png")) if IMG.exists() else []:
        if image.resolve() not in referenced:
            problems.append(f"{image.relative_to(ROOT)}: not referenced by any page")

    sys.path.insert(0, str(ROOT / "scripts" / "dev"))
    sys.path.insert(0, str(ROOT / "rhino_mcp_server" / "src"))
    try:
        from build_guide_images import SHOTS  # noqa: WPS433
        for shot in SHOTS:
            if not (IMG / f"{shot.name}.png").exists():
                problems.append(f"shot {shot.name}: no docs/guide/img/{shot.name}.png - run build_guide_images.py")
    except Exception as error:  # noqa: BLE001
        problems.append(f"could not import the shot table: {error}")

    for problem in problems:
        print(problem)
    print(f"{len(pages)} pages, {len(referenced)} images referenced, {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
