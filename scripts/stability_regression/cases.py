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
# Cantilever: a slab overhanging a narrow pedestal, swept across the tipping point
# --------------------------------------------------------------------------------------
#
# Pedestal 300 x 400 x 1000 mm over x = 0..300, carrying a 2000 x 400 x 200 mm slab that
# covers it completely, so the bearing contact is never a sliver however far the slab is
# pushed. Only the slab's position changes:
#
#     margin = 300 - (288*150 + 384*cx) / 672
#
# with 288 kg of pedestal at x = 150 and 384 kg of slab at its own centre cx. This sweeps
# a welded assembly across its own tipping point, which is the one thing welded mode is
# supposed to answer. It matters in both directions: a floor too soft tips an assembly
# whose centre of mass is still inside its base, and a floor too stiff holds up one whose
# centre of mass is outside it. Only cases either side of zero can tell those apart.

CANTILEVER_PEDESTAL = (300.0, 400.0, 1000.0)
CANTILEVER_SLAB = (2000.0, 400.0, 200.0)


def cantilever_margin_mm(slab_centre_x: float) -> float:
    pedestal = concrete_mass(*CANTILEVER_PEDESTAL)
    slab = concrete_mass(*CANTILEVER_SLAB)
    centre = (pedestal * CANTILEVER_PEDESTAL[0] / 2.0 + slab * slab_centre_x) / (pedestal + slab)
    return CANTILEVER_PEDESTAL[0] - centre


def cantilever_build(slab_centre_x: float) -> Callable[[], str]:
    def build() -> str:
        pedestal = concrete_mass(*CANTILEVER_PEDESTAL)
        slab = concrete_mass(*CANTILEVER_SLAB)
        half = CANTILEVER_SLAB[0] / 2.0
        top = CANTILEVER_PEDESTAL[2]
        return (
            f'world_box("PEDESTAL", 0.0, 0.0, 0.0, {CANTILEVER_PEDESTAL[0]!r}, '
            f'{CANTILEVER_PEDESTAL[1]!r}, {top!r}, {pedestal!r})\n'
            f'world_box("SLAB", {slab_centre_x - half!r}, 0.0, {top!r}, '
            f'{slab_centre_x + half!r}, {CANTILEVER_SLAB[1]!r}, '
            f'{top + CANTILEVER_SLAB[2]!r}, {slab!r})')

    return build


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
        name="cantilever_margin_plus120",
        mode="welded",
        tier=FAST,
        stable=True,
        reason=f"centre of mass {cantilever_margin_mm(200.0):+.0f} mm inside the pedestal face",
        build=cantilever_build(200.0),
    ),
    Case(
        name="cantilever_margin_minus40",
        mode="welded",
        tier=FAST,
        stable=False,
        reason=(
            f"centre of mass {abs(cantilever_margin_mm(482.5)):.0f} mm outside the pedestal "
            "face; the closest case to the tipping point in either direction"),
        build=cantilever_build(482.5),
    ),
    Case(
        name="cantilever_margin_minus220",
        mode="welded",
        tier=FAST,
        stable=False,
        reason=f"centre of mass {abs(cantilever_margin_mm(800.0)):.0f} mm outside the pedestal face",
        build=cantilever_build(800.0),
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
    # The unbraced bridge's four modes are INFINITESIMAL mechanisms, and the distinction
    # decides the answer. Under the mode a tie's ends separate as 2*sqrt(1 + (0.71t)^2), so
    # its length is preserved to first order and grows only at second: the structure
    # stiffens quadratically as it moves rather than collapsing, held by the five states of
    # self-stress the same rank test reports beside the modes. It stands under its own
    # weight, measurably softer than the braced version but standing.
    #
    # A rank test counts modes; it does not predict collapse. Reading "4 mechanisms" as
    # "unstable" was an inference laid on top of it, and it was wrong.
    Case(
        name="bridge_unbraced_dynamic",
        mode="pinned_dynamic",
        tier=FAST,
        stable=True,
        reason=(
            f"{BRIDGE_UNBRACED_MECHANISMS} infinitesimal mechanisms, stiffened at second "
            "order; stands under self-weight but soft in y, the direction the modes move"),
        build=bridge_build(braced=False),
        # The softness is the finding, so it is what gets asserted. Sway stiffness in the
        # mode's own direction is about 4.7e8 N/m against the braced 6.9e8, while the x
        # direction the modes do not touch is about 2.4e9 for both.
        expect={
            "sway.sway_stiffness_y_n_per_m": (4.0e8, 5.4e8),
            "sway.sway_stiffness_x_n_per_m": (2.0e9, 2.9e9),
        },
    ),
    Case(
        name="bridge_braced_dynamic",
        mode="pinned_dynamic",
        tier=FAST,
        stable=True,
        reason="0 mechanisms; stiffer in y than the unbraced bridge, 6.9e8 against 4.7e8",
        build=bridge_build(braced=True),
        expect={
            "sway.sway_stiffness_y_n_per_m": (6.0e8, 8.0e8),
            "sway.sway_stiffness_x_n_per_m": (2.0e9, 2.9e9),
        },
    ),
    # Committed failing. Two members hanging in mid-air with nothing holding them up must
    # fall: 1226 mm in half a second. They move 2.82 mm.
    #
    # The cause is structural. Each body's frame particle carries its best-fit frame and is
    # updated projectively rather than integrated, while the particles that do carry mass
    # are held to that frame by a penalty of 3.6e8 N/m - so they can depart from it by only
    # mg/k, about 1.5 micron, and the frame then follows at a quarter of that per step. A
    # free body's fall is therefore paced by the solver's update rate instead of by gravity.
    #
    # Deformation dynamics is unaffected and is separately validated: sag matches hand
    # statics, sway stiffness converges, and projected displacements match settled ones to
    # 0.1%. But gross rigid-body motion - an element toppling off its support, a fragment
    # dropping - is not simulated, so an unstable verdict can currently only be reached by
    # deformation crossing the limit. Fixing it means giving the frame the body's mass and
    # inertia and integrating it, with the pins supplying constraint forces: rigid-body
    # dynamics rather than a fitted frame.
    Case(
        name="free_fall_two_members",
        mode="pinned_dynamic",
        tier=FAST,
        stable=False,
        reason="nothing supports them; free fall is 1226 mm in 0.5 s, they move 2.82 mm",
        # The verdict alone would pass here, and passing would be misleading: with nothing
        # supporting them even 2.82 mm of travel clears the threshold this small assembly
        # gets, so "unstable" comes out right while the motion is wrong by a factor of 400.
        # What is asserted is therefore the distance, which is the thing that is broken.
        expect={"max_pin_displacement_m": (0.5, 2.0)},
        build=lambda: (
            'axis_box("A", (0.0,0.0,5000.0), (2000.0,0.0,5000.0), 150.0, 54.0)\n'
            'axis_box("B", (2000.0,0.0,5000.0), (4000.0,0.0,5000.0), 150.0, 54.0)'),
        params={"floor_z": -100000.0, "lateral_load_fraction": 0.0},
    ),
    # Committed failing, like the welded pedestal was. The relaxed pinned mode calls this
    # structure unstable through its divergence trend, which is the weaker of its two paths
    # to a verdict and the one that does not survive being checked: the integrator, a
    # notional lateral load, and the second-order reading of the mode shape all say it
    # stands. Left in the suite because a false positive that nothing asserts is a false
    # positive nobody fixes.
    Case(
        name="bridge_unbraced_relaxed",
        mode="pinned",
        tier=FAST,
        stable=True,
        reason=(
            "infinitesimal mechanisms stiffen at second order; the relaxed mode reports "
            "unstable from its divergence trend, which is a false positive"),
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
