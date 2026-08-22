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
    # Cases that ask something other than "is it stable". Given the built object ids and a
    # function to send a command, returns the problems it found - empty when it passes. The
    # solver is not run at all for these, because what is under test is upstream of it.
    check: Callable[[Callable[[str, dict], Any], list[str]], list[str]] | None = None


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
# Micro: a stack of columns whose axial shortening is a closed form
# --------------------------------------------------------------------------------------
#
# Everything else in this suite is a verdict case. These are not: they assert a
# displacement against arithmetic that can be done on paper, which is the only way to find
# out whether the joint model delivers the stiffness it claims to.
#
# A member of mass m, length L and density rho has section area A = m/(rho L), so
#
#     k = EA/L = (E/rho) m / L^2
#
# and a load W carried by n such columns in parallel shortens them by W/(nk). Two storeys
# of them add in series. Both numbers are exact, and neither depends on anything the solver
# does.
#
# Three columns, not one. A member pinned at a single point at each end is free to rotate
# about them - a chain of two such pins is an inverted pendulum, and it wanders instead of
# squashing. Three columns on a triangle fix the block against rotation and sway while
# leaving the axial direction free, which is the only freedom under test. The blocks and the
# pad are made heavy and stubby so their own EA/L is three orders above the columns' and
# contributes nothing measurable.

MICRO_COLUMN_KG = 5.4
MICRO_COLUMN_MM = 2000.0
MICRO_SECTION_MM = 150.0
# The load, and the spacer between storeys.
#
# Both are as light as the measurement allows, because cost here is set by the heaviest body
# rather than by anything under test. A body's joint stiffness is (E/rho) m / L^2, the ground
# anchor is sized at ten times the stiffest of them, and the explicit timestep is 2/omega
# against that - so a heavy stubby block pins the step at a value the columns never needed.
# Halving the load halves the deflection but also halves the stiffest stiffness, and the run
# gets cheaper in proportion. At 5 t the single storey settles in about a third of a second
# and the residual is a percent of what is being measured.
MICRO_BLOCK_KG = 5000.0
MICRO_SPACER_KG = 250.0
MICRO_PAD_KG = 4147.2
GRAVITY = 9.80665

# Plan positions of the three columns: a triangle wide enough that the block above it is
# held in rotation, inside the 1400 mm blocks that cap each storey.
MICRO_COLUMN_XY = ((-500.0, -300.0), (500.0, -300.0), (0.0, 500.0))


def micro_column_stiffness() -> float:
    """EA/L of one micro column, from its mass: (E/rho) m / L^2."""
    length = MICRO_COLUMN_MM / 1000.0
    return 210e9 * MICRO_COLUMN_KG / (7850.0 * length * length)


def micro_storey_stiffness() -> float:
    return len(MICRO_COLUMN_XY) * micro_column_stiffness()


def micro_shortening_m(storey_loads_n: list[float]) -> float:
    """Total shortening of a stack of storeys, each carrying its own load."""
    return sum(load / micro_storey_stiffness() for load in storey_loads_n)


# One storey: the block's weight, over three columns.
MICRO_ONE_STOREY_M = micro_shortening_m([MICRO_BLOCK_KG * GRAVITY])

# Two storeys: the lower one also carries the spacer block between them. The columns' own
# weight is left out of both - 53 N against 196 kN is four parts in ten thousand, well
# inside the tolerance these are asserted at.
MICRO_TWO_STOREY_M = micro_shortening_m([
    (MICRO_BLOCK_KG + MICRO_SPACER_KG) * GRAVITY,
    MICRO_BLOCK_KG * GRAVITY,
])


def _micro_pad() -> str:
    return (
        f'world_box("PAD", -1200.0, -1200.0, -300.0, 1200.0, 1200.0, 0.0, {MICRO_PAD_KG!r})')


def _micro_storey(index: int, z0: float) -> list[str]:
    lines = []
    z1 = z0 + MICRO_COLUMN_MM
    for slot, (x, y) in enumerate(MICRO_COLUMN_XY):
        lines.append(
            f'axis_box("COL_{index}_{slot}", ({x!r},{y!r},{z0!r}), ({x!r},{y!r},{z1!r}), '
            f'{MICRO_SECTION_MM!r}, {MICRO_COLUMN_KG!r})')
    return lines


def _micro_block(name: str, z0: float, mass: float) -> str:
    return (
        f'world_box("{name}", -700.0, -700.0, {z0!r}, 700.0, 700.0, {z0 + 200.0!r}, {mass!r})')


def micro_stack_build(storeys: int) -> Callable[[], str]:
    def build() -> str:
        lines = [_micro_pad()]
        z = 0.0
        for index in range(storeys):
            lines.extend(_micro_storey(index, z))
            z += MICRO_COLUMN_MM
            top = index == storeys - 1
            lines.append(_micro_block(
                f"BLOCK_{index}", z, MICRO_BLOCK_KG if top else MICRO_SPACER_KG))
            z += 200.0
        return "\n".join(lines)

    return build


# --------------------------------------------------------------------------------------
# Splayed legs: the same members, leaning
# --------------------------------------------------------------------------------------
#
# The upright stack tests axial stiffness along the world Z axis, which is the one direction
# that hides a whole class of error - a member's length read off a world-aligned box is right
# only when the member lies along an axis, and every stiffness here goes as 1/L^2. Leaning
# the same legs by 30 degrees changes nothing about the members and everything about the
# arithmetic.
#
# Three legs of length L at an angle theta from vertical, carrying W. Each takes W/(3 cos
# theta) along its own axis and shortens by that over k, and the block descends by the
# shortening over cos theta again:
#
#     delta = W / (3 k cos^2 theta)
#
# At 30 degrees cos^2 is 3/4, so the same legs under the same load must deflect exactly 4/3
# of what they do upright. That ratio depends on nothing but the angle.

MICRO_SPLAY_DEG = 30.0
MICRO_SPLAY_TOP_RADIUS = 200.0


def micro_splay_geometry() -> tuple[float, float, float]:
    """Bottom radius, top radius and height for legs of MICRO_COLUMN_MM at MICRO_SPLAY_DEG."""
    lean = math.radians(MICRO_SPLAY_DEG)
    bottom = MICRO_SPLAY_TOP_RADIUS + MICRO_COLUMN_MM * math.sin(lean)
    return bottom, MICRO_SPLAY_TOP_RADIUS, MICRO_COLUMN_MM * math.cos(lean)


def micro_splay_shortening_m() -> float:
    lean = math.radians(MICRO_SPLAY_DEG)
    return (MICRO_BLOCK_KG * GRAVITY) / (micro_storey_stiffness() * math.cos(lean) ** 2)


MICRO_SPLAY_M = micro_splay_shortening_m()


def micro_splay_build() -> str:
    bottom, top, height = micro_splay_geometry()
    # A wide, light pad: it has to span the splayed feet without becoming the stiffest body
    # in the model, since the timestep is set by whichever body that is while a pad
    # contributes about a percent of the answer.
    lines = ['world_box("PAD", -1800.0, -1800.0, -200.0, 1800.0, 1800.0, 0.0, 2000.0)']
    for slot in range(3):
        angle = math.radians(120.0 * slot)
        lines.append(
            f'axis_box("LEG_{slot}", '
            f'({bottom * math.cos(angle)!r},{bottom * math.sin(angle)!r},0.0), '
            f'({top * math.cos(angle)!r},{top * math.sin(angle)!r},{height!r}), '
            f'{MICRO_SECTION_MM!r}, {MICRO_COLUMN_KG!r})')
    lines.append(
        f'world_box("BLOCK", -500.0, -500.0, {height!r}, 500.0, 500.0, '
        f'{height + 200.0!r}, {MICRO_BLOCK_KG!r})')
    return "\n".join(lines)


# Isolate the axial question: no notional load, no built-in imperfection, nothing to settle
# but the weight itself.
MICRO_PARAMS = {"lateral_load_fraction": 0.0, "imperfection_fraction": 0.0}


# These cases measure a settled deflection, so they are run with enough damping to reach one
# inside the run. That is a legitimate knob here and would not be in a verdict case: a static
# deflection does not depend on damping, only the time taken to arrive at it does.
#
# The two integrators need different amounts, and the difference is itself a finding. The
# particle path damps each particle against its own local stiffness, which over-damps the slow
# global mode - at 2% it creeps up to the answer monotonically and never overshoots, and at 30%
# it reaches only a quarter of the answer in half a second. The rigid path damps each joint
# against relative motion at that joint, which barely touches a mode where both ends move
# together, so it needs far more of it. Neither ratio means what a code means by 2% of
# critical, and this is where that is on record.
#
# The rigid path's figure was 20% while a per-body dashpot on absolute angular velocity was
# also running. That term had to go - it damps the very motion an element makes as it falls
# off its support, so nothing could ever topple - and taking it out removed most of what was
# settling these cases: the one-storey stack drifted to 0.552 mm against an exact 0.453, and
# the splayed one to 11 mm against 0.603. At 100% both land inside their bands, 0.467 and
# 0.661, and the splayed case is closer to the closed form than it ever was. The damping did
# not change; what was measuring it did.
MICRO_DAMPING = {
    "particles": {},
    "rigid_bodies": {"damping_ratio": 1.0},
}


def micro_expect(exact_m: float) -> dict[str, tuple[float, float]]:
    # Asserted on where it came to rest, not on the furthest it went. A load applied suddenly
    # overshoots to twice its static deflection and rings back - correct physics, and the right
    # thing for a verdict to judge, but the wrong thing to calibrate against: a well-damped
    # integrator and an over-damped one report different peaks for the same structure. The
    # settled figure means nothing unless the run reached a conclusion, so that is asserted
    # too rather than assumed.
    #
    # Ten percent. The arithmetic is exact and the model's own contaminants - the columns'
    # weight, the pad's shortening, the ground anchor's give - are under one percent between
    # them. The rest is the settling residual, which is what a run that stops after half a
    # second of real time has left over.
    return {
        "conclusive": (1.0, 1.0),
        "settled_displacement_m": (exact_m * 0.90, exact_m * 1.10),
    }


# --------------------------------------------------------------------------------------
# Bearing extent: contacts whose size and orientation are known by construction
# --------------------------------------------------------------------------------------
#
# A joint at a point transmits no moment, because a point has no lever arm. Two springs of
# stiffness k separated by d resist rotation with k d^2, so whether a wall behaves like a
# wall or like a pin-ended strut is decided by whether its bearing has a measured size. The
# graph samples that region and reduces it to a plane and a rectangle; these cases check the
# reduction against footprints that are known because they were drawn.
#
# The rotated wall is the case that matters. An axis-aligned bounding box gets the first two
# right and cannot get the third right at all - it would report a 1146 x 671 mm world-aligned
# box for a 1150 x 150 mm wall turned 30 degrees. Anything that reproduces 1150 x 150 in that
# orientation is not using a world box.

EXTENT_PAD = (-3000.0, -2000.0, -300.0, 3000.0, 2000.0, 0.0)
EXTENT_WALL_LENGTH = 1150.0
EXTENT_WALL_THICKNESS = 150.0
EXTENT_COLUMN_SIDE = 150.0
EXTENT_WALL_ROTATION_DEG = 30.0


def extent_scene() -> str:
    pad_mass = concrete_mass(6000.0, 4000.0, 300.0)
    return f"""
import math
import Rhino
world_box("PAD", {EXTENT_PAD[0]!r}, {EXTENT_PAD[1]!r}, {EXTENT_PAD[2]!r},
          {EXTENT_PAD[3]!r}, {EXTENT_PAD[4]!r}, {EXTENT_PAD[5]!r}, {pad_mass!r})
world_box("WALL", -2500.0, -1500.0, 0.0,
          {-2500.0 + EXTENT_WALL_LENGTH!r}, {-1500.0 + EXTENT_WALL_THICKNESS!r}, 2000.0, 400.0)
axis_box("COLUMN", (-500.0,-1425.0,0.0), (-500.0,-1425.0,2000.0), {EXTENT_COLUMN_SIDE!r}, 54.0)
ang = math.radians({EXTENT_WALL_ROTATION_DEG!r})
plane = Rhino.Geometry.Plane(
    Rhino.Geometry.Point3d(2000.0, 800.0, 1000.0),
    Rhino.Geometry.Vector3d(math.cos(ang), math.sin(ang), 0.0),
    Rhino.Geometry.Vector3d(-math.sin(ang), math.cos(ang), 0.0))
add_box("WALL_ROT30", plane, {EXTENT_WALL_LENGTH!r}, {EXTENT_WALL_THICKNESS!r}, 2000.0, 400.0)
"""


# name -> (long side, short side, bearing of the long axis) from the drawn geometry. The
# angle is None where the footprint is square and its long axis is therefore arbitrary.
EXTENT_EXPECTED = {
    "WALL": (EXTENT_WALL_LENGTH, EXTENT_WALL_THICKNESS, 0.0),
    "WALL_ROT30": (EXTENT_WALL_LENGTH, EXTENT_WALL_THICKNESS, EXTENT_WALL_ROTATION_DEG),
    "COLUMN": (EXTENT_COLUMN_SIDE, EXTENT_COLUMN_SIDE, None),
}

# Five percent. The sampling walks a grid across the smaller element, so an edge sample can
# sit up to one spacing short of the true boundary; measured, the worst side is 152 against
# 150, or 1.3%. There is no risk of admitting a wrong answer with room to spare - the rotated
# wall's world axis-aligned box is 1071 x 705, which is 370% out on the short side.
EXTENT_TOLERANCE = 0.05

# The long axis is the whole point for a wall: a rectangle of the right size lying in the
# wrong direction restrains the wrong rotation. Measured within 0.1 degree.
EXTENT_ANGLE_TOLERANCE_DEG = 2.0


def check_extent(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    """Every bearing's measured rectangle against the footprint it was drawn with."""
    graph = send("get_connectivity_graph", {"ids": ids})
    names = {node["i"]: node.get("name", "") for node in graph.get("n", [])}
    measured = graph.get("contact_extent") or []

    problems = []
    seen = set()
    for entry in measured:
        # Every contact here is an element bearing on the pad, so the element is whichever
        # end of the edge is not the pad.
        pair = [names.get(entry["a"], ""), names.get(entry["b"], "")]
        named = [name for name in pair if name in EXTENT_EXPECTED]
        if len(named) != 1:
            continue

        name = named[0]
        seen.add(name)
        want_long, want_short, want_angle = EXTENT_EXPECTED[name]
        sides = [(entry["length_u"], entry["u"]), (entry["length_v"], entry["v"])]
        sides.sort(key=lambda pair: pair[0], reverse=True)
        (got_long, long_axis), (got_short, _) = sides
        for label, want, got_value in (
            ("long", want_long, got_long), ("short", want_short, got_short)
        ):
            if abs(got_value - want) > want * EXTENT_TOLERANCE:
                problems.append(
                    f"{name} bearing {label} side {got_value:.0f} mm, expected {want:.0f}")

        # Which way the long side runs. A rectangle of the right size in the wrong direction
        # restrains the wrong rotation, and it is the one thing a world-aligned box can never
        # get right for a rotated element.
        if want_angle is not None:
            got_angle = math.degrees(math.atan2(long_axis[1], long_axis[0])) % 180.0
            off = abs(got_angle - (want_angle % 180.0))
            off = min(off, 180.0 - off)
            if off > EXTENT_ANGLE_TOLERANCE_DEG:
                problems.append(
                    f"{name} bearing long axis at {got_angle:.1f} deg, expected {want_angle:.1f}")

        # A bearing on a level pad is horizontal, so its normal is vertical. A rectangle
        # fitted to the wrong plane passes the side lengths and fails here.
        normal = entry.get("normal") or [0.0, 0.0, 0.0]
        if abs(abs(normal[2]) - 1.0) > 1e-3:
            problems.append(f"{name} bearing normal {normal}, expected vertical")

    for name in EXTENT_EXPECTED:
        if name not in seen:
            problems.append(f"{name} has no measured bearing extent")

    return problems


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

# A third tier, asking a different question from either. Fast and slow both ask whether the
# verdict is right; micro asks whether a number is right, against a closed form. It is the
# tier that can say an integrator is wrong rather than merely disagreeing with the other
# one, so it is what decides which of the two survives.
MICRO = "micro"


GEOMETRY = "geometry"


CASES: list[Case] = [
    Case(
        name="bearing_extent",
        mode="none",
        tier=GEOMETRY,
        stable=True,
        reason=(
            "a 1150 x 150 mm wall, a 150 mm column and the same wall turned 30 degrees; "
            "an axis-aligned box reports the third as 1146 x 671"),
        build=extent_scene,
        check=check_extent,
    ),
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
    # The same three questions, asked of the multi-body solver with the joints named
    # "contact" instead of asked of the relaxed contact solver. They are the gate on folding
    # the two together: the answers were established by hand statics, the contact mode has
    # been answering them for weeks, and a second implementation that agrees is what lets the
    # first one go.
    Case(
        name="stair3_step100_contact_joint",
        mode="pinned_dynamic",
        tier=FAST,
        stable=True,
        reason=f"joint margin {stair_margin_mm(100.0):+.0f} mm (600/2 - 1.5*100)",
        build=stair_build(100.0),
        params={
            "integrator": "rigid_bodies",
            "joint_type": "contact",
            "damping_ratio": 0.2,
            "lateral_load_fraction": 0.0,
        },
    ),
    Case(
        name="stair3_step300_contact_joint",
        mode="pinned_dynamic",
        tier=FAST,
        stable=False,
        reason=(
            f"joint margin {stair_margin_mm(300.0):+.0f} mm; the bearing opens under the "
            "overhanging tread and it rotates off"),
        build=stair_build(300.0),
        params={
            "integrator": "rigid_bodies",
            "joint_type": "contact",
            "damping_ratio": 0.2,
            "lateral_load_fraction": 0.0,
        },
    ),
    Case(
        name="pedestal_eccentric_contact_joint",
        mode="pinned_dynamic",
        tier=FAST,
        stable=False,
        reason=f"cap centre of mass {abs(PEDESTAL_MARGIN_MM):.0f} mm beyond the pedestal face",
        build=pedestal_build,
        params={
            "integrator": "rigid_bodies",
            "joint_type": "contact",
            "damping_ratio": 0.2,
            "lateral_load_fraction": 0.0,
        },
    ),
    # The direct test that naming the joint reaches the solver at all, on one geometry that
    # cannot answer both ways by accident. A welded bearing resists rotation with k d^2 over
    # its measured width and holds the stack; a pin collapses the same bearing to its centre,
    # where a point has no lever arm, and three blocks stacked on three points is a mechanism.
    # If these two agree, the type is being dropped somewhere between the parameter and the
    # site - which is the failure mode a stiffness comparison would not catch.
    Case(
        name="stair3_step100_welded_joint",
        mode="pinned_dynamic",
        tier=FAST,
        stable=True,
        reason="a welded bearing carries moment over its measured width, so the stack stands",
        build=stair_build(100.0),
        params={
            "integrator": "rigid_bodies",
            "joint_type": "welded",
            "damping_ratio": 0.2,
            "lateral_load_fraction": 0.0,
        },
    ),
    Case(
        name="stair3_step100_pin_joint",
        mode="pinned_dynamic",
        tier=FAST,
        stable=False,
        reason=(
            "a pin collapses the bearing to its centre, and a point carries no moment - three "
            "blocks stacked on three points is a mechanism whatever the margin"),
        build=stair_build(100.0),
        params={
            "integrator": "rigid_bodies",
            "joint_type": "pin",
            "damping_ratio": 0.2,
            "lateral_load_fraction": 0.0,
        },
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
        # mode's own direction is about 1.13e9 N/m against the braced 1.66e9, while the x
        # direction the modes do not touch is near 5e9 for both.
        #
        # These bounds doubled when EndSpringsInSeries landed, because every stiffness this
        # evaluator had ever reported was half of what it claimed - see the micro tier, where
        # three columns of known EA/L settled it. The y figures moved by 2.4 rather than 2.0,
        # which is itself the expected signature: this is a secant stiffness measured on a
        # mechanism that stiffens quadratically as it moves, so halving the sway makes it
        # relatively stiffer still, while the linear x direction moved by 2.1. What did not
        # move is the ratio the case exists to show - braced over unbraced, 1.46 against the
        # old 1.48.
        #
        # They then fell by 0.71 when member length stopped being read off the world
        # axis-aligned bounding box. Twenty of this bridge's forty members are diagonal webs
        # and every brace is a diagonal, and a tilted member's box is shorter than the member
        # - 1.41 m for a 2 m web - so k, going as 1/L^2, came out 1.7 times too stiff for
        # exactly those members. The x direction barely moved, because the chords carrying it
        # are axis-aligned and were never affected. The ratio survived again, 1.41.
        expect={
            "sway.sway_stiffness_y_n_per_m": (7.2e8, 9.2e8),
            "sway.sway_stiffness_x_n_per_m": (4.5e9, 5.8e9),
        },
        # The probe is off by default now, and these are the cases that exist to measure it.
        params={"lateral_load_fraction": 0.05},
    ),
    Case(
        name="bridge_braced_dynamic",
        mode="pinned_dynamic",
        tier=FAST,
        stable=True,
        reason="0 mechanisms; stiffer in y than the unbraced bridge, 1.66e9 against 1.13e9",
        build=bridge_build(braced=True),
        expect={
            "sway.sway_stiffness_y_n_per_m": (1.02e9, 1.28e9),
            "sway.sway_stiffness_x_n_per_m": (4.3e9, 5.5e9),
        },
        params={"lateral_load_fraction": 0.05},
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
        tier=MICRO,
        stable=False,
        reason="nothing supports them; they must fall at g, reaching the 10 mm limit in 0.045 s",
        # The verdict alone proves nothing here - with nothing holding them up, even the
        # 2.82 mm the old particle integrator managed cleared the threshold, so "unstable"
        # came out right while the motion was wrong by a factor of 400. What is asserted is
        # the *rate*: the run stops the moment displacement crosses the limit, so the time
        # at which that happens is a direct measurement of the acceleration. Falling 10 mm
        # under gravity takes sqrt(2 * 0.010 / 9.81) = 0.045 s, and anything slower is a
        # body not falling at g.
        expect={"simulated_seconds": (0.030, 0.075)},
        build=lambda: (
            'axis_box("A", (0.0,0.0,5000.0), (2000.0,0.0,5000.0), 150.0, 54.0)\n'
            'axis_box("B", (2000.0,0.0,5000.0), (4000.0,0.0,5000.0), 150.0, 54.0)'),
        params={
            "floor_z": -100000.0,
            "lateral_load_fraction": 0.0,
            # No disturbance, so what is measured is gravity and nothing else: the jolt is
            # 0.198 m/s here, comparable to the fall itself over so short a time.
            "imperfection_fraction": 0.0,
            # The rigid-body integrator, which is what can represent free motion at all.
            # The default particle integrator fails this case by a factor of 400 and is
            # covered separately below.
            "integrator": "rigid_bodies",
        },
    ),
    # The same drop on the default integrator, committed failing. It is the case that
    # motivated the rigid-body work: a body there is a handful of particles held to a fitted
    # frame that is measured rather than integrated, so it can leave that frame by only
    # mg/k - about 1.5 micron - and descends at the solver's update rate instead of at g.
    Case(
        name="free_fall_two_members_particles",
        mode="pinned_dynamic",
        tier=MICRO,
        stable=False,
        reason="the particle integrator cannot represent free motion; it reaches 0.2% of g",
        build=lambda: (
            'axis_box("A", (0.0,0.0,5000.0), (2000.0,0.0,5000.0), 150.0, 54.0)\n'
            'axis_box("B", (2000.0,0.0,5000.0), (4000.0,0.0,5000.0), 150.0, 54.0)'),
        expect={"simulated_seconds": (0.030, 0.075)},
        params={
            "floor_z": -100000.0,
            "lateral_load_fraction": 0.0,
            "imperfection_fraction": 0.0,
            "integrator": "particles",
        },
    ),
    # This case was committed failing for as long as a relaxed pinned solver existed. It
    # called the structure unstable through its divergence trend - the weaker of its two
    # paths to a verdict, and the one that did not survive being checked, firing at 1.47 mm
    # of pin motion against a 60.8 mm limit while an integrator, a lateral load test and the
    # mode shape all said it stands.
    #
    # It passes now because "pinned" is an alias for the dynamic solver and the relaxed one
    # is deleted. The defect is gone rather than tolerated, and the assertion never moved.
    Case(
        name="bridge_unbraced_pinned_alias",
        mode="pinned",
        tier=FAST,
        stable=True,
        reason=(
            "infinitesimal mechanisms stiffen at second order, so it stands; and \"pinned\" "
            "must now answer identically to \"pinned_dynamic\""),
        build=bridge_build(braced=False),
    ),
]


# The axial pair, run against both integrators. Four cases rather than two because the
# question they settle is which integrator to keep, and that cannot be asked of one at a
# time.
for _integrator in ("particles", "rigid_bodies"):
    CASES.append(Case(
        name=f"axial_one_storey_{_integrator}",
        mode="pinned_dynamic",
        tier=MICRO,
        stable=True,
        reason=(
            f"{MICRO_BLOCK_KG * GRAVITY / 1000.0:.1f} kN over three columns of "
            f"{micro_column_stiffness():.3e} N/m shortens them "
            f"{MICRO_ONE_STOREY_M * 1000.0:.3f} mm"),
        build=micro_stack_build(1),
        expect=micro_expect(MICRO_ONE_STOREY_M),
        params=dict(MICRO_PARAMS, **MICRO_DAMPING[_integrator], integrator=_integrator),
    ))
    CASES.append(Case(
        name=f"axial_splayed_{_integrator}",
        mode="pinned_dynamic",
        tier=MICRO,
        stable=True,
        reason=(
            f"the same three legs leaning {MICRO_SPLAY_DEG:.0f} degrees deflect "
            f"W/(3k cos^2) = {MICRO_SPLAY_M * 1000.0:.3f} mm, exactly 4/3 of the "
            f"{MICRO_ONE_STOREY_M * 1000.0:.3f} mm they give upright"),
        build=micro_splay_build,
        expect=micro_expect(MICRO_SPLAY_M),
        params=dict(MICRO_PARAMS, **MICRO_DAMPING[_integrator], integrator=_integrator),
    ))
    CASES.append(Case(
        name=f"axial_two_storeys_{_integrator}",
        mode="pinned_dynamic",
        tier=MICRO,
        stable=True,
        reason=(
            f"two storeys in series, the lower also carrying the spacer: "
            f"{MICRO_TWO_STOREY_M * 1000.0:.3f} mm against the single storey's "
            f"{MICRO_ONE_STOREY_M * 1000.0:.3f}"),
        # Committed failing on the rigid-body integrator, at 0.780 mm against 0.928, and the
        # cause is worth more than the case.
        #
        # The spacer between the storeys is 200 mm thick, and the clustering radius is the
        # body's own smallest dimension - so its top and bottom faces sit at exactly that
        # radius and merge. The middle nodes come back as three bodies meeting at one point,
        # 200 mm from where either face is, instead of two nodes with the spacer between
        # them. Merging them is defensible: a 200 mm plate of 3.4e9 N/m contributes no
        # compliance worth modelling.
        #
        # What is not defensible is that it changes the answer, and only for one integrator.
        # The particle path keeps a member's compliance in its body-to-frame springs, which do
        # not care how many bodies share a node, and passes. The rigid path keeps all of it in
        # the joint springs at 2k per end, so removing a joint from a load path removes a
        # spring from the series and stiffens the whole stack by 16%. **A member's stiffness
        # there depends on how many joints it happens to have**, which is a property of the
        # mesh rather than of the member.
        #
        # Two things to fix, in this order: the joint stiffness should be shared out along a
        # member's load path rather than fixed at 2k per end, and the clustering radius should
        # come from the contact patch rather than the body's smallest dimension, where opposite
        # faces of any plate land exactly on the threshold and merge on a tie.
        build=micro_stack_build(2),
        expect=micro_expect(MICRO_TWO_STOREY_M),
        params=dict(MICRO_PARAMS, **MICRO_DAMPING[_integrator], integrator=_integrator),
    ))


def by_name(name: str) -> Case:
    for case in CASES:
        if case.name == name:
            return case
    raise KeyError(name)


def in_tier(tier: str) -> list[Case]:
    return [case for case in CASES if case.tier == tier]
