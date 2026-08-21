"""Stability regression cases whose answers come from hand statics, not the solver.

Every case is built from code rather than loaded from a .3dm, so the geometry and the
independently-derived answer live in the same place and cannot drift apart. Each builder
returns the RhinoScript that draws it; each case carries the margin or mechanism count that
settles it, and the arithmetic that produced them is written out beside them.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass, field
from typing import Any, Callable


# The suite works in a millimetre document, matching the models these cases were first
# found in. Units are part of what is under test: a verdict that changes with the document
# unit is a bug, and there is a separate imperial cross-check for that.
DOCUMENT_UNIT = "Millimeters"

CONCRETE_DENSITY = 2400.0


@dataclass
class Case:
    name: str
    mode: str
    tier: str
    stable: bool
    # Why the answer is what it is, in one line, traced to statics or a rank test.
    reason: str
    build: Callable[[], str]
    # Optional numeric assertions: key in the result -> (low, high) in the result's own unit.
    expect: dict[str, tuple[float, float]] = field(default_factory=dict)
    params: dict[str, Any] = field(default_factory=dict)


# --------------------------------------------------------------------------------------
# Script helpers
# --------------------------------------------------------------------------------------

PREAMBLE = f"""
import Rhino
import scriptcontext
import json

doc = scriptcontext.doc
doc.AdjustModelUnitSystem(Rhino.UnitSystem.{DOCUMENT_UNIT}, False)

built = []


def clear():
    for obj in list(doc.Objects):
        doc.Objects.Delete(obj, True)
    doc.Objects.Clear()


def add_box(name, plane, dx, dy, dz, mass):
    box = Rhino.Geometry.Box(
        plane,
        Rhino.Geometry.Interval(-dx / 2.0, dx / 2.0),
        Rhino.Geometry.Interval(-dy / 2.0, dy / 2.0),
        Rhino.Geometry.Interval(-dz / 2.0, dz / 2.0))
    attrs = Rhino.DocObjects.ObjectAttributes()
    attrs.Name = name
    attrs.SetUserString("rhinomcp.stability.v1", '{{"mass": %r, "mass_unit": "kg"}}' % mass)
    object_id = doc.Objects.AddBrep(box.ToBrep(), attrs)
    built.append(str(object_id))
    return object_id


def axis_box(name, a, b, side, mass):
    "A square-section bar drawn along the segment a-b."
    start = Rhino.Geometry.Point3d(*a)
    end = Rhino.Geometry.Point3d(*b)
    axis = end - start
    length = axis.Length
    plane = Rhino.Geometry.Plane(start + axis * 0.5, axis)
    return add_box(name, plane, side, side, length, mass)


def world_box(name, x0, y0, z0, x1, y1, z1, mass):
    plane = Rhino.Geometry.Plane(
        Rhino.Geometry.Point3d((x0 + x1) / 2.0, (y0 + y1) / 2.0, (z0 + z1) / 2.0),
        Rhino.Geometry.Vector3d.ZAxis)
    return add_box(name, plane, x1 - x0, y1 - y0, z1 - z0, mass)


clear()
"""

EPILOGUE = """
doc.Views.Redraw()
print("RHINOMCP_BUILT=" + json.dumps(built))
"""


def script(body: str) -> str:
    return PREAMBLE + body + EPILOGUE


def concrete_mass(dx_mm: float, dy_mm: float, dz_mm: float) -> float:
    return CONCRETE_DENSITY * (dx_mm / 1000.0) * (dy_mm / 1000.0) * (dz_mm / 1000.0)


# --------------------------------------------------------------------------------------
# Stair: three blocks, each offset by one step
# --------------------------------------------------------------------------------------
#
# Blocks are 600 x 600 x 300 mm, block i running from x = i*s to i*s + 600.
#
# The joint that decides it is the one under block 1, not the ground. Block 1 carries
# blocks 1 and 2, whose combined centre of mass sits at x = 1.5s + 300. Its overlap with
# block 0 ends at x = 600. So
#
#     margin = 600 - (1.5s + 300) = 300 - 1.5s
#
# and the stack stands only while s < 200 mm. The ground joint is slacker - the whole
# assembly's centre of mass is at x = s + 300 over a base ending at 600, giving 300 - s -
# so it never governs in the range tested here.

STAIR_BLOCK = 600.0
STAIR_HEIGHT = 300.0


def stair_margin_mm(step: float) -> float:
    return STAIR_BLOCK / 2.0 - 1.5 * step


def stair_build(step: float) -> Callable[[], str]:
    def build() -> str:
        mass = concrete_mass(STAIR_BLOCK, STAIR_BLOCK, STAIR_HEIGHT)
        lines = []
        for i in range(3):
            x0 = i * step
            z0 = i * STAIR_HEIGHT
            lines.append(
                f'world_box("STAIR_{i}", {x0!r}, 0.0, {z0!r}, '
                f'{x0 + STAIR_BLOCK!r}, {STAIR_BLOCK!r}, {z0 + STAIR_HEIGHT!r}, {mass!r})')
        return "\n".join(lines)

    return build


# --------------------------------------------------------------------------------------
# Pedestal: a wide block sitting eccentrically on a narrow one
# --------------------------------------------------------------------------------------
#
# Pedestal 400 x 400 x 1000 mm over x = 0..400. The block on top is 1000 x 400 x 200 mm
# over x = 150..1150, so its centre of mass is at x = 650 while the pedestal it stands on
# ends at x = 400:
#
#     margin = 400 - 650 = -250 mm
#
# It has to rotate off. This is the case that separates the two modes most sharply: welded
# sees one body whose combined centre of mass is still inside the pedestal's footprint,
# and calls it stable; contact sees the block leave its support.

PEDESTAL_MARGIN_MM = -250.0


def pedestal_build() -> str:
    pedestal_mass = concrete_mass(400.0, 400.0, 1000.0)
    block_mass = concrete_mass(1000.0, 400.0, 200.0)
    return (
        f'world_box("PEDESTAL", 0.0, 0.0, 0.0, 400.0, 400.0, 1000.0, '
        f'{pedestal_mass!r})\n'
        f'world_box("CAP", 150.0, 0.0, 1000.0, 1150.0, 400.0, 1200.0, '
        f'{block_mass!r})'
    )


# --------------------------------------------------------------------------------------
# Bridge: a triangular-prism Warren girder over a 10 m span
# --------------------------------------------------------------------------------------
#
# Bottom nodes at (2i, +/-1, 0) for i = 0..5; ridge nodes at (2i+1, 0, sqrt(2)) for
# i = 0..4. Every one of the 40 members is then exactly 2000 mm:
#
#     bottom chords   (2i,+/-1,0)-(2i+2,+/-1,0)     10 members, length 2
#     transverse ties (2i,-1,0)-(2i,+1,0)            6 members, length 2
#     webs            (2i,+/-1,0)-(2i+1,0,sqrt2)    20 members, length sqrt(1+1+2) = 2
#     top chords      (2i+1,0,s2)-(2i+3,0,s2)        4 members, length 2
#
# The ridge height falls out of the requirement, not the other way round: 1 + 1 + h^2 = 4
# gives h = sqrt(2). Members are drawn as 150 mm solid boxes but massed as SHS 150x150x6 at
# 54 kg, which is why the evaluator has to recover section area from mass rather than from
# the drawn volume.
#
# Unbraced this is a mechanism. The bottom plane is a row of 2 x 2 m squares with nothing
# on their diagonals, and a square panel cannot be braced by a member of its own edge
# length - the diagonal is edge x sqrt(2) = 2828 mm. A rigid-body rank test finds 4
# independent mechanisms, each local to one interior transverse tie and moving it
# (0, +0.82, -/+0.58): a slide along the tie plus a seesaw, in the ratio sqrt(2):1. Every
# member is length-preserving under it, which is why nothing resists it.
#
# Adding 5 zigzag diagonals of 2828 mm across the bottom panels takes the count to 0.

BRIDGE_SPAN_M = 10.0
BRIDGE_RIDGE_M = math.sqrt(2.0)
BRIDGE_MEMBER_KG = 54.0
BRIDGE_BRACE_KG = 76.4
BRIDGE_SECTION_MM = 150.0
BRIDGE_MEMBER_COUNT = 40
BRIDGE_BRACE_COUNT = 5
BRIDGE_UNBRACED_MECHANISMS = 4

PAD_X = (0.0, BRIDGE_SPAN_M)
PAD_SIZE_M = (2.0, 2.0, 0.3)


def _bridge_nodes() -> tuple[list[tuple[float, float, float]], list[tuple[float, float, float]]]:
    bottom = [(2.0 * i, y, 0.0) for i in range(6) for y in (-1.0, 1.0)]
    ridge = [(2.0 * i + 1.0, 0.0, BRIDGE_RIDGE_M) for i in range(5)]
    return bottom, ridge


def bridge_members() -> list[tuple[tuple[float, float, float], tuple[float, float, float]]]:
    members = []
    for y in (-1.0, 1.0):
        for i in range(5):
            members.append(((2.0 * i, y, 0.0), (2.0 * i + 2.0, y, 0.0)))
    for i in range(6):
        members.append(((2.0 * i, -1.0, 0.0), (2.0 * i, 1.0, 0.0)))
    for i in range(5):
        apex = (2.0 * i + 1.0, 0.0, BRIDGE_RIDGE_M)
        for dx in (0.0, 2.0):
            for y in (-1.0, 1.0):
                members.append(((2.0 * i + dx, y, 0.0), apex))
    for i in range(4):
        members.append((
            (2.0 * i + 1.0, 0.0, BRIDGE_RIDGE_M),
            (2.0 * i + 3.0, 0.0, BRIDGE_RIDGE_M)))
    return members


def bridge_braces() -> list[tuple[tuple[float, float, float], tuple[float, float, float]]]:
    # Zigzag so consecutive panels take opposite diagonals: one continuous load path along
    # the bottom plane rather than five independent ones.
    braces = []
    for i in range(5):
        if i % 2 == 0:
            braces.append(((2.0 * i, -1.0, 0.0), (2.0 * i + 2.0, 1.0, 0.0)))
        else:
            braces.append(((2.0 * i, 1.0, 0.0), (2.0 * i + 2.0, -1.0, 0.0)))
    return braces


def bridge_build(braced: bool) -> Callable[[], str]:
    def build() -> str:
        lines = []
        pad_mass = concrete_mass(
            PAD_SIZE_M[0] * 1000.0, PAD_SIZE_M[1] * 1000.0, PAD_SIZE_M[2] * 1000.0)
        for index, x in enumerate(PAD_X):
            lines.append(
                f'world_box("PAD_{index}", '
                f'{(x - PAD_SIZE_M[0] / 2.0) * 1000.0!r}, {-PAD_SIZE_M[1] / 2.0 * 1000.0!r}, '
                f'{-PAD_SIZE_M[2] * 1000.0!r}, '
                f'{(x + PAD_SIZE_M[0] / 2.0) * 1000.0!r}, {PAD_SIZE_M[1] / 2.0 * 1000.0!r}, '
                f'0.0, {pad_mass!r})')

        for index, (a, b) in enumerate(bridge_members()):
            am = tuple(v * 1000.0 for v in a)
            bm = tuple(v * 1000.0 for v in b)
            lines.append(
                f'axis_box("M_{index:02d}", {am!r}, {bm!r}, '
                f'{BRIDGE_SECTION_MM!r}, {BRIDGE_MEMBER_KG!r})')

        if braced:
            for index, (a, b) in enumerate(bridge_braces()):
                am = tuple(v * 1000.0 for v in a)
                bm = tuple(v * 1000.0 for v in b)
                lines.append(
                    f'axis_box("BR_{index}", {am!r}, {bm!r}, '
                    f'{BRIDGE_SECTION_MM!r}, {BRIDGE_BRACE_KG!r})')

        return "\n".join(lines)

    return build


# --------------------------------------------------------------------------------------
# The suite
# --------------------------------------------------------------------------------------
#
# Tiers. The fast tier runs at the shipped default budget and holds only cases that are
# already correct there, so it is usable on every edit. The slow tier sweeps the budget
# upward and records the smallest one that reaches the right answer. That ceiling is the
# measurement: relaxation converges toward equilibrium rather than falling, so today a
# mechanism creeps and the verdict depends on how long it is allowed to creep. A dynamics
# rewrite should collapse these ceilings toward the default; if it does not, the suite says
# so in a number instead of staying green.
#
# Moving a case from the fast tier into the slow tier is therefore a regression in itself,
# and is meant to be visible in the diff.

FAST = "fast"
SLOW = "slow"


CASES: list[Case] = [
    Case(
        name="stair3_step100",
        mode="contact",
        tier=FAST,
        stable=True,
        reason=f"joint margin {stair_margin_mm(100.0):+.0f} mm (600/2 - 1.5*100)",
        build=stair_build(100.0),
    ),
    Case(
        name="stair3_step300",
        mode="contact",
        tier=FAST,
        stable=False,
        reason=f"joint margin {stair_margin_mm(300.0):+.0f} mm, centre of mass clear of the overlap",
        build=stair_build(300.0),
    ),
    Case(
        name="stair3_step250",
        mode="contact",
        tier=SLOW,
        stable=False,
        reason=(
            f"joint margin {stair_margin_mm(250.0):+.0f} mm; inside the known blind band "
            "where contact mode has read stable"),
        build=stair_build(250.0),
    ),
    Case(
        name="pedestal_eccentric",
        mode="contact",
        tier=FAST,
        stable=False,
        reason=f"cap centre of mass {abs(PEDESTAL_MARGIN_MM):.0f} mm beyond the pedestal face",
        build=pedestal_build,
    ),
    Case(
        name="pedestal_welded_reference",
        mode="welded",
        tier=FAST,
        stable=True,
        reason=(
            "welded mode cannot see one element leave another; the combined centre of mass "
            "is still over the pedestal, so stable here is correct for what this mode asks"),
        build=pedestal_build,
    ),
    Case(
        name="bridge_braced",
        mode="pinned",
        tier=FAST,
        stable=True,
        reason="rank test finds 0 mechanisms; hand midspan sag about 1.8 mm",
        build=bridge_build(braced=True),
        expect={"max_pin_displacement_m": (0.0, 0.005)},
    ),
    Case(
        name="bridge_unbraced",
        mode="pinned",
        tier=SLOW,
        stable=False,
        reason=(
            f"rank test finds {BRIDGE_UNBRACED_MECHANISMS} mechanisms, one per interior "
            "transverse tie; the bottom plane is unbraced squares"),
        build=bridge_build(braced=False),
    ),
]


def by_name(name: str) -> Case:
    for case in CASES:
        if case.name == name:
            return case
    raise KeyError(name)


def in_tier(tier: str) -> list[Case]:
    return [case for case in CASES if case.tier == tier]
