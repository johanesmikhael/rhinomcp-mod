#!/usr/bin/env python3
"""Draw a few regression cases into Rhino and save them as demo files.

The regression suite builds its models in Rhino and throws them away, so the cases with
the clearest answers exist only for as long as a test run. This writes some of them to
disk instead, to be opened by hand.

It writes to whatever document Rhino has open, clearing it first, so run it against a
scratch document - never against something you want to keep.

    python3 scripts/dev/build_demo_files.py
"""

from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "stability_regression"))
sys.path.insert(0, str(ROOT / "rhino_mcp_server" / "src"))

import cases as case_module
from rhinomcp.server import RhinoConnection

OUT = ROOT / "RhinoAndGHFiles"

# name -> (case build body, joint-type rules to store, one-line note)
DEMOS = {
    "stair_jointtypes": (
        case_module.stair_build(100.0),
        [],
        "Three blocks, each set 100 mm forward. Stands as contact, topples as pin.",
    ),
    "stair_toppling": (
        case_module.stair_build(300.0),
        [],
        "The same stair at 300 mm. The centre of mass clears the bearing, so it goes over.",
    ),
    "pavilion_jointtypes": (
        case_module.pavilion_build(),
        [],
        "Four walls in a pinwheel with a roof set on them - nothing fixed to anything.",
    ),
}


def execute(connection: RhinoConnection, code: str) -> str:
    result = connection.send_command("execute_rhinoscript_python_code", {"code": code})
    if result.get("success") is not True:
        raise SystemExit(f"build failed: {result.get('message')}")
    return result.get("result", "")


def save_as(connection: RhinoConnection, path: pathlib.Path) -> None:
    """Write the document through RhinoCommon rather than through SaveAs.

    run_command prefixes an underscore, so a dashed command arrives as "_-_SaveAs" and is
    not a command at all; and the undashed one opens a file dialog nobody is there to
    answer. WriteFile asks nothing.
    """
    code = (
        "import Rhino, scriptcontext as sc\n"
        "opts = Rhino.FileIO.FileWriteOptions()\n"
        f"ok = sc.doc.WriteFile(r'{path}', opts)\n"
        "print('WROTE=%s' % ok)\n"
    )
    result = connection.send_command(
        "execute_rhinoscript_python_code", {"code": code})
    if "WROTE=True" not in (result.get("result") or ""):
        raise SystemExit(f"save failed for {path}: {result}")
    print(f"  saved {path.name}")


def main() -> int:
    connection = RhinoConnection(host="127.0.0.1", port=1999)
    connection.connect()
    for name, (build, rules, note) in DEMOS.items():
        body = build() if callable(build) else build
        print(f"{name}: {note}")
        execute(connection, case_module.script(body))

        # Joint-type rules live in document user text, so whatever the scratch document was
        # last used for is still in it and would be saved into the demo. Prune is not enough:
        # it drops rules that can no longer name anything, and a rule naming a layer the
        # scratch document still has can still name it. Delete the key instead, then write
        # only the rules this demo means to carry.
        execute(connection,
                "import scriptcontext as sc\n"
                "sc.doc.Strings.Delete('rhinomcp.stability.joint_types.v1')\n"
                "print('RULES_CLEARED')\n")
        for rule in rules:
            connection.send_command("assign_joint_type", rule)
        save_as(connection, OUT / f"{name}.3dm")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
