#!/usr/bin/env python3
"""Capture every image in docs/guide/img/ from a running Rhino.

Each shot opens a demo file, puts the plugin into a known state (an overlay on, an
evaluation run), captures the viewport through the plugin's own capture_view, and writes
the PNG. One run regenerates the whole set, so a change to what the plugin draws is a rerun
away from the guide.

Talks to the plugin socket directly (the MCP tool returns the image to the model, not to a
file). Needs Rhino on 127.0.0.1:1999 with the 0.4.0 plugin loaded; demo files are opened
read-only and never saved.

    python3 scripts/dev/build_guide_images.py            # every shot
    python3 scripts/dev/build_guide_images.py --only graph-stair
    python3 scripts/dev/build_guide_images.py --sway     # also dump both bridges' sway blocks
"""

from __future__ import annotations

import argparse
import base64
import json
import pathlib
import sys
from dataclasses import dataclass, field
from typing import Any, Callable

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "rhino_mcp_server" / "src"))

from rhinomcp.server import RhinoConnection  # noqa: E402

DEMO = ROOT / "RhinoAndGHFiles"
OUT = ROOT / "docs" / "guide" / "img"
REQUIRED_VERSION = "0.4.0"

Step = tuple[str, dict[str, Any]] | Callable[["Session"], None]


@dataclass
class Shot:
    name: str
    open: str
    capture: dict[str, Any]
    setup: list[Step] = field(default_factory=list)
    teardown: list[Step] = field(default_factory=list)


class Session:
    def __init__(self, connection: RhinoConnection, dump_dir: pathlib.Path | None):
        self.connection = connection
        self.dump_dir = dump_dir
        self.responses: dict[str, Any] = {}

    def send(self, command: str, params: dict[str, Any] | None = None, timeout: float | None = None) -> Any:
        params = params or {}
        # A capture of a large model can want more than the 15 s default.
        result = self.connection.send_command(command, params, timeout=timeout)
        if isinstance(result, dict) and result.get("error"):
            raise SystemExit(f"{command} failed: {result['error']}")
        if isinstance(result, dict) and result.get("success") is False:
            raise SystemExit(f"{command} failed: {result.get('message')}")
        return result

    def run_command(self, text: str, expect: str) -> None:
        """A Rhino command with its option tokens, verified on the command line.

        A prompting command left waiting swallows the calls that follow it, so the log
        has to show the command finished before this returns.
        """
        result = self.send("run_command", {"command": text})
        message = result.get("message", "") if isinstance(result, dict) else str(result)
        if "returned false" in message:
            raise SystemExit(f"'{text}' did not run: {message}")
        log = self.send("get_log", {"lines": 6})
        entries = log.get("entries", []) if isinstance(log, dict) else []
        text_log = "\n".join(str(entry) for entry in entries) if entries else str(log)
        if expect not in text_log:
            raise SystemExit(f"'{text}' ran but the command line does not say '{expect}':\n{text_log}")

    def open(self, name: str) -> None:
        path = DEMO / name
        if not path.exists():
            raise SystemExit(f"demo file missing: {path}")
        try:
            self.send("open_file", {"path": str(path), "close_current": True, "save_current": False})
        except Exception as error:  # noqa: BLE001 - the connection wraps everything in Exception
            if "No active Rhino document" not in str(error):
                raise
            # Nothing to close: a bare Rhino, or a previous run left it that way.
            self.send("open_file", {"path": str(path), "close_current": False})

    def dump(self, name: str, payload: Any) -> None:
        if self.dump_dir is None:
            return
        self.dump_dir.mkdir(parents=True, exist_ok=True)
        (self.dump_dir / f"{name}.json").write_text(json.dumps(payload, indent=1, default=str))


# A 30 degrees rotation about world Z, as create_objects takes it.
COS30, SIN30 = 0.8660254, 0.5
TURNED = [[COS30, -SIN30, 0], [SIN30, COS30, 0], [0, 0, 1]]

SOLID_NAMES = ("BLOCK", "TURNED_BLOCK", "BALL", "POST", "LAID_POST", "CAP")

PRIMITIVES = [
    {"type": "BOX", "name": "BLOCK", "params": {"width": 400, "length": 260, "height": 180},
     "translation": [0, 0, 90], "color": [176, 176, 176]},
    {"type": "SPHERE", "name": "BALL", "params": {"radius": 110},
     "translation": [520, 0, 110], "color": [176, 176, 176]},
    {"type": "CYLINDER", "name": "POST", "params": {"radius": 70, "height": 300, "cap": True, "axis": "z"},
     "translation": [860, 0, 150], "color": [176, 176, 176]},
    {"type": "CONE", "name": "CAP", "params": {"radius": 110, "height": 240, "cap": True, "axis": "z"},
     "translation": [1140, 0, 120], "color": [176, 176, 176]},
    {"type": "BOX", "name": "TURNED_BLOCK", "params": {"width": 600, "length": 240, "height": 200},
     "translation": [520, 700, 100], "rotation_matrix": TURNED, "color": [176, 176, 176]},
    {"type": "CYLINDER", "name": "LAID_POST",
     "params": {"radius": 90, "height": 700, "cap": True, "axis": "x"},
     "translation": [1140, 700, 90], "color": [176, 176, 176]},
    {"type": "POLYLINE", "name": "PATH",
     "params": {"points": [[-200, 320, 0], [1340, 320, 0], [1340, 320, 400]]}},
    {"type": "CIRCLE", "name": "RING", "params": {"center": [520, -340, 0], "radius": 180}},
]


def obb_on(session: Session) -> None:
    # The toggle prints only when it changes state, so ask Status for the verdict.
    session.send("run_command", {"command": "mcpmodobb On"})
    session.run_command("mcpmodobb Status", "MCP OBB is ON")


def build_shapes(session: Session) -> None:
    """Write RhinoAndGHFiles/guide_shapes.3dm: one of each primitive, two of them turned.

    Built in a document opened from another demo file and written out under a new name, so
    nothing existing is overwritten. Rhino cannot close an untitled document through the
    plugin, which is why this is a file rather than a scratch document.
    """
    session.open("stair_jointtypes.3dm")
    session.send("execute_rhinoscript_python_code", {"code": "\n".join([
        "import Rhino, scriptcontext as sc",
        "doc = sc.doc",
        "[doc.Objects.Delete(o, True) for o in list(doc.Objects)]",
        "keys = [doc.Strings.GetKey(i) for i in range(doc.Strings.Count)]",
        "[doc.Strings.Delete(key) for key in keys]",
        # one layer named SHAPES, so the file's listing is about the shapes and not
        # about whatever the document it was built from happened to carry
        "kept = doc.Layers.Add('SHAPES', System.Drawing.Color.FromArgb(120, 120, 120))"
        if False else "kept = doc.Layers.Add('SHAPES', Rhino.ApplicationSettings.AppearanceSettings.DefaultLayerColor)",
        "doc.Layers.SetCurrentLayerIndex(kept, True)",
        "[doc.Layers.Delete(i, True) for i in range(doc.Layers.Count) if i != kept]",
        "[doc.Materials.Delete(doc.Materials[i]) for i in range(doc.Materials.Count)]",
        "doc.Views.Redraw()",
    ])})
    # The socket command takes one entry per object keyed by name, not a list; the MCP
    # tool does that reshaping itself.
    session.dump("guide-shapes-create",
                 session.send("create_objects", {spec["name"]: spec for spec in PRIMITIVES}))
    material = session.send("create_material", {"name": "guide oak", "r": 190, "g": 150, "b": 96})
    session.dump("guide-shapes-material", material)
    listing = session.send("get_document_info", {"limit": 100})
    solids = [o["id"] for o in listing["objects"] if o["name"] in SOLID_NAMES]
    # By index, not by name: assigning by name adds a material per object.
    session.send("set_object_material", {"ids": solids, "material_index": material["index"]})
    out = DEMO / "guide_shapes.3dm"
    session.send("execute_rhinoscript_python_code", {"code": "\n".join([
        "import Rhino, scriptcontext as sc",
        "doc = sc.doc",
        f"ok = doc.WriteFile(r'{out}', Rhino.FileIO.FileWriteOptions())",
        "doc.Modified = False",
        "print('written' if ok else 'FAILED')",
    ])})
    print(f"wrote {out.relative_to(ROOT)}")


def graph_on(session: Session) -> None:
    session.send("graph_display", {"enabled": True, "scope": "all"})


def graph_off(session: Session) -> None:
    session.send("graph_display", {"enabled": False})


def settled_pose_on(session: Session) -> None:
    result = session.send(
        "evaluate_stability",
        {"mode": "elements", "joint_type": "contact", "display": True},
    )
    session.dump("stability-stair-toppling", result)


def settled_pose_off(session: Session) -> None:
    try:
        session.run_command("mcpmodstabilitydisplay Off", "OFF")
    except SystemExit:
        # The command's off switch is also reachable through the evaluator.
        session.send("evaluate_stability", {"mode": "elements", "joint_type": "contact", "display": False})


def obb_off(session: Session) -> None:
    session.send("run_command", {"command": "mcpmodobb Off"})
    session.run_command("mcpmodobb Status", "MCP OBB is OFF")


# White background and a print-size capture: the readout and markers scale with the size,
# and the figure is read at a fraction of it on the page. Shaded with the display mode's
# own object colour; the plugin drops the mode's background and flattens onto white.
SHADED_HIGH = {
    "all_visible": True, "display_mode": "Shaded", "resolution": "print",
    "background": "white", "padding": 1.05,
}

SHOTS: list[Shot] = [
    Shot(
        "graph-stair", "stair_jointtypes.3dm",
        capture={"view": "perspective", **SHADED_HIGH},
        setup=[graph_on], teardown=[graph_off],
    ),
    Shot(
        "joint-types-portal", "timber_bridge.3dm",
        capture={"view": "perspective", **SHADED_HIGH},
        setup=[graph_on], teardown=[graph_off],
    ),
    Shot(
        "stability-stair-settled", "stair_toppling.3dm",
        capture={"view": "front", **SHADED_HIGH},
        setup=[settled_pose_on], teardown=[settled_pose_off],
    ),
    Shot(
        "bridges-xbraced-iso", "timber_bridge_xbraced.3dm",
        capture={"view": "isometric", **SHADED_HIGH},
    ),
    Shot(
        "bridges-portal-iso", "timber_bridge.3dm",
        capture={"view": "isometric", **SHADED_HIGH},
    ),
    Shot(
        "bridges-xbraced-graph", "timber_bridge_xbraced.3dm",
        capture={"view": "isometric", **SHADED_HIGH},
        setup=[graph_on], teardown=[graph_off],
    ),
    Shot(
        "bridges-portal-graph", "timber_bridge.3dm",
        capture={"view": "isometric", **SHADED_HIGH},
        setup=[graph_on], teardown=[graph_off],
    ),
    Shot(
        "geometry-primitives", "guide_shapes.3dm",
        capture={"view": "perspective", **SHADED_HIGH, "display_mode": "Rendered"},
    ),
    Shot(
        "pose-obb", "guide_shapes.3dm",
        capture={"view": "perspective", **SHADED_HIGH},
        setup=[obb_on], teardown=[obb_off],
    ),
    Shot(
        "views-technical", "timber_bridge.3dm",
        capture={"view": "front", **SHADED_HIGH, "display_mode": "Technical"},
    ),
    Shot(
        "views-camera-explicit", "timber_bridge_xbraced.3dm",
        capture={"all_visible": True, "display_mode": "Shaded", "resolution": "print",
                 "background": "white", "fit": False,
                 "camera_location": [-9000, -14000, 7000], "camera_target": [9000, 0, 1500],
                 "lens_mm": 85},
    ),
]


def run_steps(session: Session, steps: list[Step]) -> None:
    for step in steps:
        if callable(step):
            step(session)
        else:
            command, params = step
            session.send(command, params)


def capture(session: Session, shot: Shot) -> tuple[int, int]:
    result = session.send("capture_view", shot.capture, timeout=60.0)
    png = result.get("png_base64") if isinstance(result, dict) else None
    if not png:
        raise SystemExit(f"{shot.name}: capture_view returned no image: {json.dumps(result)[:300]}")
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / f"{shot.name}.png").write_bytes(base64.b64decode(png))
    metadata = result.get("metadata", {})
    session.dump(f"capture-{shot.name}", metadata)
    return int(metadata.get("width", 0)), int(metadata.get("height", 0))


def sway(session: Session) -> None:
    """The two bridges with the sway probe, for the worked example."""
    for name in ("timber_bridge_xbraced.3dm", "timber_bridge.3dm"):
        session.open(name)
        result = session.send(
            "evaluate_stability",
            {"mode": "elements", "display": False, "lateral_load_fraction": 0.05},
        )
        session.dump(f"sway-{pathlib.Path(name).stem}", result)
        block = result.get("sway") or {}
        print(f"{name}: stable={result.get('stable')} "
              f"x {block.get('sway_stiffness_x_n_per_m')} N/m, y {block.get('sway_stiffness_y_n_per_m')} N/m, "
              f"softest {block.get('softest_direction')}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--only", nargs="*", default=None, help="shot names to capture")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--dump-json", type=pathlib.Path, default=None,
                        help="write every response next to the images, for quoting in the pages")
    parser.add_argument("--require-version", default=REQUIRED_VERSION)
    parser.add_argument("--sway", action="store_true", help="also run the sway probe on both bridges")
    parser.add_argument("--build-shapes", action="store_true",
                        help="rewrite RhinoAndGHFiles/guide_shapes.3dm, the file the geometry shots use")
    args = parser.parse_args()

    if args.list:
        for shot in SHOTS:
            print(f"{shot.name:28} {shot.open or '(scratch document)':28} {shot.capture}")
        return 0

    selected = SHOTS if not args.only else [s for s in SHOTS if s.name in set(args.only)]
    missing = set(args.only or []) - {s.name for s in selected}
    if missing:
        raise SystemExit(f"unknown shots: {sorted(missing)}")

    connection = RhinoConnection(host="127.0.0.1", port=1999)
    if not connection.connect():
        print("Rhino is not listening on 127.0.0.1:1999; open a document and run mcpmodstart", file=sys.stderr)
        return 2
    session = Session(connection, args.dump_json)
    # Every plugin handler, the version command included, needs an active document.
    session.open((selected or SHOTS)[0].open if not args.build_shapes else SHOTS[0].open)
    session.run_command("mcpmodversion", args.require_version)

    failures = 0
    try:
        if args.build_shapes:
            build_shapes(session)
        for shot in selected:
            session.open(shot.open)
            try:
                run_steps(session, shot.setup)
                width, height = capture(session, shot)
                print(f"wrote {OUT.relative_to(ROOT)}/{shot.name}.png ({width}x{height})")
            finally:
                run_steps(session, shot.teardown)
        if args.sway:
            sway(session)
    except (SystemExit, Exception) as error:  # noqa: BLE001 - report, clean up, exit non-zero
        print(f"FAIL {error}", file=sys.stderr)
        failures = 1
    finally:
        # Leave nothing drawn, whatever happened above. The last demo file stays open,
        # unsaved: a Rhino with no document answers nothing, so closing it would leave the
        # next caller with "No active Rhino document".
        for step in (graph_off, obb_off, settled_pose_off):
            try:
                step(session)
            except (SystemExit, Exception):  # noqa: BLE001 - a dead socket must not mask the real failure
                pass
    return failures


if __name__ == "__main__":
    sys.exit(main())
