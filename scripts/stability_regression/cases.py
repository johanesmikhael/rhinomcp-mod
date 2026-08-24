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


def layer_index(name):
    "The named layer, made if it is not there. Add returns -1 for one that already exists."
    existing = doc.Layers.FindName(name, -1)
    if existing is not None:
        return existing.Index
    layer = Rhino.DocObjects.Layer()
    layer.Name = name
    return doc.Layers.Add(layer)


def add_box(name, plane, dx, dy, dz, mass, layer=None):
    box = Rhino.Geometry.Box(
        plane,
        Rhino.Geometry.Interval(-dx / 2.0, dx / 2.0),
        Rhino.Geometry.Interval(-dy / 2.0, dy / 2.0),
        Rhino.Geometry.Interval(-dz / 2.0, dz / 2.0))
    attrs = Rhino.DocObjects.ObjectAttributes()
    attrs.Name = name
    if layer is not None:
        attrs.LayerIndex = layer_index(layer)
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


def world_box(name, x0, y0, z0, x1, y1, z1, mass, layer=None):
    plane = Rhino.Geometry.Plane(
        Rhino.Geometry.Point3d((x0 + x1) / 2.0, (y0 + y1) / 2.0, (z0 + z1) / 2.0),
        Rhino.Geometry.Vector3d.ZAxis)
    return add_box(name, plane, x1 - x0, y1 - y0, z1 - z0, mass, layer)


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
# Joint-type rules: the same stair, answered by whichever rule is most specific
# --------------------------------------------------------------------------------------
#
# The three blocks of the +150 mm stair, the bottom one on layer STEP_A and the two above
# it on STEP_B, so the two joints belong to different element-class pairs: the lower is
# A-to-B and the upper is B-to-B. That is what makes precedence observable - a rule that
# matches one joint and not the other has to show up as one node changing type and the
# other not.
#
# Four states, one per branch of the resolution:
#
#   no rules                    both welded, from evaluate_stability's own default
#   pair rule A x B = pin       lower pin ("pair:"), upper still welded ("default")
#   element rule on STEP_B      lower pin ("element:b"), upper pin ("element:both")
#   cleared                     both welded again
#
# The verdicts come along for free and are the same physics as stair3_step100_pin_joint: a
# stack held at points is a mechanism whatever its margin, so any state with a pin in it is
# unstable and the two welded states stand. That is the check that the resolved type reaches
# the solver rather than merely being reported.

RULE_STAIR_STEP = 100.0


def rule_stair_build() -> str:
    mass = concrete_mass(STAIR_BLOCK, STAIR_BLOCK, STAIR_HEIGHT)
    lines = []
    for i in range(3):
        x0 = i * RULE_STAIR_STEP
        z0 = i * STAIR_HEIGHT
        layer = "STEP_A" if i == 0 else "STEP_B"
        lines.append(
            f'world_box("STAIR_{i}", {x0!r}, 0.0, {z0!r}, '
            f'{x0 + STAIR_BLOCK!r}, {STAIR_BLOCK!r}, {z0 + STAIR_HEIGHT!r}, {mass!r}, '
            f'{layer!r})')
    return "\n".join(lines)


RULE_EVAL = {
    "mode": "pinned_dynamic",
    "integrator": "rigid_bodies",
    "joint_type": "welded",
    "damping_ratio": 0.2,
    "lateral_load_fraction": 0.0,
    "gravity": 9.80665,
    "solver_substeps": 1,
    "display": False,
}


def check_joint_type_rules(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    problems = []

    def clear_rules() -> None:
        # Both kinds, because both persist: pair rules in document text and element rules on
        # the objects. A rule left behind would silently change every later case in the run,
        # which is the sort of coupling a suite is supposed to be free of.
        send("assign_joint_type", {"clear": True, "layer": "STEP_A", "with_layer": "STEP_B"})
        send("assign_joint_type", {"clear": True, "layer": "STEP_B", "with_layer": "STEP_B"})
        send("assign_joint_type",
             {"clear": True, "ids": [ids[1]], "with_ids": [ids[2]]})
        send("assign_joint_type", {"clear": True, "ids": ids})

    def nodes_by_type(label: str, expect_stable: bool) -> list[tuple[str, str]]:
        result = send("evaluate_stability", dict(RULE_EVAL, ids=ids))
        if result.get("success") is not True:
            problems.append(f"{label}: {result.get('message')}")
            return []
        if bool(result.get("stable")) != expect_stable:
            problems.append(
                f"{label}: {result.get('verdict')}, expected "
                f"{'stable' if expect_stable else 'unstable'}")
        return sorted(
            (node.get("joint_type"), node.get("joint_type_rule"))
            for node in result.get("nodes") or [])

    clear_rules()

    got = nodes_by_type("no rules", True)
    if got != [("welded", "default"), ("welded", "default")]:
        problems.append(f"no rules: {got}, expected both welded from the default")

    send("assign_joint_type",
         {"joint_type": "pin", "layer": "STEP_A", "with_layer": "STEP_B"})
    got = nodes_by_type("pair rule", False)
    if got != [("pin", "pair:STEP_A|STEP_B"), ("welded", "default")]:
        problems.append(
            f"pair rule: {got}, expected the A-to-B joint pinned and the B-to-B joint left")

    send("assign_joint_type", {"clear": True, "layer": "STEP_A", "with_layer": "STEP_B"})
    send("assign_joint_type", {"joint_type": "pin", "layer": "STEP_B"})
    got = nodes_by_type("element rule", False)
    if got != [("pin", "element:both"), ("pin", "element:one")]:
        problems.append(
            f"element rule: {got}, expected one joint resolved by one element and one by both")

    clear_rules()

    # A rule naming two objects beats one naming their layers. Without that, the tighter rule
    # is unstatable: every joint between the two classes would have to move together, and
    # "this one connection is different" is exactly the case a per-joint rule exists for.
    send("assign_joint_type", {"joint_type": "pin", "layer": "STEP_B", "with_layer": "STEP_B"})
    send("assign_joint_type",
         {"joint_type": "welded", "ids": [ids[1]], "with_ids": [ids[2]]})
    got = nodes_by_type("id pair over layer pair", True)
    types = sorted(entry[0] for entry in got)
    rules = sorted(entry[1] for entry in got)
    if types != ["welded", "welded"]:
        problems.append(
            f"id pair over layer pair: {got}, expected the object rule to override the layer one")
    if sum(rule.startswith("pair:id:") for rule in rules) != 1:
        problems.append(f"id pair over layer pair: {rules}, expected one rule named by object")

    clear_rules()
    got = nodes_by_type("cleared", True)
    if got != [("welded", "default"), ("welded", "default")]:
        problems.append(f"cleared: {got}, expected the rules to be gone")

    return problems


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
# Hybrid: a concrete frame carrying a mass-timber deck
# --------------------------------------------------------------------------------------
#
# Two portal frames 4 m apart, each a pair of 400 mm concrete columns on 700 mm pads, with a
# glulam beam across their heads and CLT panels spanning between the two beams. One geometry,
# built four ways, and the difference between the verdicts is the point.
#
# The materials are five times apart in density and within 2% in specific stiffness, which is
# the whole reason this model is worth having: E/rho is 2.68e7 for steel and 2.62e7 for C24
# spruce, and the evaluator only ever uses that ratio, so timber and concrete members of the
# same size differ in what they weigh and hardly at all in how stiff they are. A model that
# has only ever seen one material cannot tell those two facts apart.
#
# Dimensions in mm, and every mass follows from geometry times density, so the total weight is
# an independent check on the whole build - a unit error anywhere shows up there before any
# solver runs.

TIMBER_GLULAM_DENSITY = 470.0      # GL24h, near enough
TIMBER_CLT_DENSITY = 480.0         # C24 lamellae

HYBRID_PAD = (700.0, 700.0, 250.0)
HYBRID_COLUMN = (400.0, 400.0, 3000.0)
HYBRID_BEAM_DEPTH = 400.0
HYBRID_BEAM_WIDTH = 200.0
HYBRID_PANEL_THICKNESS = 160.0

HYBRID_SPAN_X = 6000.0             # between column centres, along the beam
HYBRID_SPAN_Y = 4000.0             # between the two frames, spanned by the panels
HYBRID_PANEL_COUNT = 4

HYBRID_COLUMN_TOP = HYBRID_COLUMN[2]
HYBRID_BEAM_TOP = HYBRID_COLUMN_TOP + HYBRID_BEAM_DEPTH

# How far a panel lands on the beam under it. The beam is 200 wide and centred on the frame
# line, so a panel reaching the frame line bears half the beam's width.
HYBRID_BEARING = HYBRID_BEAM_WIDTH / 2.0

# How far the defective panel reaches. Long enough that its centre of mass is 1800 mm past
# the one bearing it has, and short enough to clear the far beam's near face at 3900 by a
# margin no clustering tolerance can close.
#
# Do not read the reported displacement as a margin. The run stops as soon as the motion
# crosses the mechanism limit, so an unstable case always reports a figure barely above it -
# 41.5 mm against 40.9 - however decisively it is failing. What says this one is decisive is
# that it crosses in 36k steps where the sound model runs the full 167k without ever getting
# near.
HYBRID_SHORT_PANEL = 3800.0


# Set by the runner's --show flag: draw the graph and leave the settled geometry on screen.
SHOW_WORK = False


# --------------------------------------------------------------------------------------
# A joint that can only hold so much
# --------------------------------------------------------------------------------------
#
# A post, a 2 m arm cantilevered off it, and a weight hung under the arm's tip. The arm's
# joint to the post carries the whole cantilever moment, so it is the joint a capacity has to
# bind on - and it binds on the *moment*, which is the case a net force cannot see: the joint
# is in net compression at -7.1 kN while one of its bearing points is pulled at 24.5 kN.
#
# The three states are the point of the case. No capacity behaves exactly as the model always
# did. A capacity larger than the demand changes nothing at all, which is what says the limit
# is a limit and not a stiffness. A capacity smaller than the demand yields, the arm rotates,
# and the verdict says so.
SKEW_POST = (300.0, 300.0, 3000.0)
SKEW_ARM_REACH = 2000.0
SKEW_HANGER_KG = 400.0

# Measured, and asserted so it cannot drift unnoticed: the most any one bearing point of the
# arm's joint is pulled, with nothing limiting it.
SKEW_PEAK_POINT_N = 24472.0
SKEW_PEAK_TOLERANCE = 0.05


def capacity_scene() -> str:
    return f"""
world_box('POST', 0.0, 0.0, 0.0, {SKEW_POST[0]!r}, {SKEW_POST[1]!r}, {SKEW_POST[2]!r}, 500.0)
world_box('ARM', 0.0, 0.0, {SKEW_POST[2]!r}, {SKEW_ARM_REACH!r}, {SKEW_POST[1]!r},
          {SKEW_POST[2] + 300.0!r}, 300.0)
world_box('HANGER', {SKEW_ARM_REACH - 400.0!r}, 0.0, {SKEW_POST[2] - 600.0!r},
          {SKEW_ARM_REACH - 100.0!r}, {SKEW_POST[1]!r}, {SKEW_POST[2]!r}, {SKEW_HANGER_KG!r})
"""


def check_capacity(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    """Unlimited, generous, and too small - and only the last of them changes anything."""

    def run(capacity_kn):
        send("assign_joint_type", {"clear": True, "ids": ids})
        if capacity_kn is not None:
            send("assign_joint_type",
                 {"joint_type": "welded", "capacity_kn": capacity_kn, "ids": ids})
        return send("evaluate_stability", {
            "mode": "pinned_dynamic",
            "integrator": "rigid_bodies",
            "ids": ids,
            "gravity": GRAVITY,
            "solver_substeps": 1,
            "display": SHOW_WORK,
            "joint_type": "welded",
            "damping_ratio": 0.2,
            "lateral_load_fraction": 0.0,
        })

    send("assign_joint_type", {"prune": True})
    problems = []

    unlimited = run(None)
    if not unlimited.get("stable"):
        problems.append("the model does not stand before any capacity is stated")

    peak = max(
        (j.get("peak_point_tension_n") or 0.0) for j in (unlimited.get("joint_forces") or [{}]))
    if abs(peak - SKEW_PEAK_POINT_N) > SKEW_PEAK_TOLERANCE * SKEW_PEAK_POINT_N:
        problems.append(f"peak bearing-point tension {peak:.0f} N against {SKEW_PEAK_POINT_N:.0f}")

    # Generous: four points share 200 kN, so 50 kN each against a demand of 24.5. Nothing
    # should change, and "nothing" includes the deflection to the millimetre.
    generous = run(200.0)
    if generous.get("joints_at_capacity"):
        problems.append(
            f"{generous['joints_at_capacity']} joints reached a limit twice their demand")

    before = unlimited.get("max_pin_displacement_m") or 0.0
    after = generous.get("max_pin_displacement_m") or 0.0
    if abs(after - before) > 1e-6:
        problems.append(
            f"a capacity nothing reaches moved the answer, {1000*before:.3f} to {1000*after:.3f} mm")

    # Too small: 2.5 kN a point against 24.5 of demand. The joint yields and the arm goes.
    small = run(10.0)
    if not small.get("joints_at_capacity"):
        problems.append("no joint reached a limit an order of magnitude under its demand")

    if small.get("stable"):
        problems.append("the arm stands on a joint that cannot hold its moment")

    send("assign_joint_type", {"clear": True, "ids": ids})
    return problems


# --------------------------------------------------------------------------------------
# What the joints carry, against statics done by hand
# --------------------------------------------------------------------------------------
#
# The three micro columns stand at (-500,-300), (500,-300) and (0,500) under a block centred
# on the origin, so the block's weight does not divide equally between them. Equilibrium about
# the block's centre gives it exactly:
#
#     2a + b = W          the three reactions carry the weight
#     -600a + 500b = 0    and put no moment into the block
#     -> a = W/3.2,  b = 1.2a
#
# That is a closed form, so it says whether a reported force is right rather than whether two
# runs agree. It also pins the sign convention: a column under a block is in compression, so
# its tension reads negative.
MICRO_REACTION_TOLERANCE = 0.02


def check_joint_forces(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    """Reported joint forces against the reactions statics requires."""
    result = send("evaluate_stability", {
        "mode": "pinned_dynamic",
        "integrator": "rigid_bodies",
        "ids": ids,
        "gravity": GRAVITY,
        "solver_substeps": 1,
        "display": SHOW_WORK,
        "joint_type": "welded",
        "damping_ratio": MICRO_DAMPING["rigid_bodies"]["damping_ratio"],
        **MICRO_PARAMS,
    })

    forces = result.get("joint_forces") or []
    if not forces:
        return ["no joint forces reported"]

    weight = MICRO_BLOCK_KG * GRAVITY
    near = weight / 3.2
    far = 1.2 * near

    problems = []

    # Six joints: three columns to the block above and three to the pad below. The two sides
    # differ only by the columns' own weight, so their magnitudes interleave and cannot be
    # told apart by sorting - which is what a first version of this check tried to do.
    #
    # Two things pin the answer without needing to separate them. The ratio between the
    # largest and the smallest is the ratio statics demands, 1.2, and it is a pure statement
    # about the distribution. The sum over all six is the block's weight counted twice, once
    # into the columns and once out of them, plus the columns' own.
    if len(forces) != 6:
        problems.append(f"{len(forces)} joints reported, expected 6")

    magnitudes = sorted(f["force_n"] for f in forces)
    if magnitudes[0] > 0.0:
        ratio = magnitudes[-1] / magnitudes[0]
        if abs(ratio - far / near) > MICRO_REACTION_TOLERANCE * (far / near):
            problems.append(
                f"largest reaction is {ratio:.3f} times the smallest, expected "
                f"{far / near:.3f}")

    columns = 3.0 * MICRO_COLUMN_KG * GRAVITY
    want_total = 2.0 * weight + columns
    total = sum(magnitudes)
    if abs(total - want_total) > MICRO_REACTION_TOLERANCE * want_total:
        problems.append(f"reactions sum to {total:.0f} N against {want_total:.0f} N")

    # A column under a block is in compression, whatever else is true.
    for f in forces:
        tension = f.get("tension_n")
        if tension is None:
            problems.append("a joint reported no sense, so it had no measured bearing plane")
        elif tension > 0.0:
            problems.append(f"a joint reads {tension:.0f} N of tension under a block resting on it")

    return problems


# --------------------------------------------------------------------------------------
# A truss set down on its pads, rather than bolted to them
# --------------------------------------------------------------------------------------
#
# The same braced bridge, with one thing stated that no other case states: its members are
# bolted to one another, and the whole truss merely rests on its two pads. Every bridge case
# before this one declared a single joint type for the entire model, so the supports - the
# part of a real bridge most likely to move - were assumed to be as rigid as the truss.
#
# It should stand. A statically determinate truss under vertical load puts vertical reactions
# into its supports and nothing else; there is no thrust to spread, which is an arch's
# problem and not a truss's. Friction at 0.6 has almost nothing to resist.
#
# COMMITTED FAILING, and the reason is worth more than the case. Clustering merges every
# contact within a body's own smallest dimension into one node, so at each support the bottom
# chord, a vertical, a diagonal, a brace and the pad all become a single node of five bodies:
#
#     contact (element:one)  bodies 5  z=+0.010  ['BR_0','M_00','M_10','M_16','PAD_0']
#
# Weakest-governs then makes that whole node contact, including the truss's own bolted
# connections - which are in tension there - and every pair at the site shares the node's one
# bearing normal. So the member-to-member joints open under a downward pull, 32 joints
# separate, and the truss comes apart at 0.6 m/s.
#
# Two physically different joints are being merged because they are at the same point: "these
# members are bolted to each other" and "this assembly rests on that pad". Resolving it needs
# a joint type per pair within a node rather than per node, which is a change to how sites are
# built and not something to slip in behind a test.
BRIDGE_PAD_NAMES = ("PAD_0", "PAD_1")


def check_bridge_on_pads(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    """Truss bolted to itself, resting on its pads."""
    inventory = send("get_document_info", {"limit": 200})
    pads = [
        obj["id"] for obj in inventory.get("objects", [])
        if obj.get("name") in BRIDGE_PAD_NAMES
    ]
    if len(pads) != len(BRIDGE_PAD_NAMES):
        return [f"expected {len(BRIDGE_PAD_NAMES)} pads, found {len(pads)}"]

    # Rules outlive a case, so this states its own from a clean table.
    send("assign_joint_type", {"prune": True})
    send("assign_joint_type", {"clear": True, "ids": pads})

    # An element rule, not a pair rule: every joint a pad has is a bearing, whichever member
    # is on the other side of it. Weakest-governs does the rest, contact being weaker than
    # the pin the truss is declared with.
    send("assign_joint_type", {"joint_type": "contact", "ids": pads})

    if SHOW_WORK:
        send("graph_display", {"enabled": True, "ids": ids})

    result = send("evaluate_stability", {
        "mode": "pinned_dynamic",
        "integrator": "rigid_bodies",
        "ids": ids,
        "gravity": GRAVITY,
        "display": SHOW_WORK,
        "solver_substeps": 1,
        "lateral_load_fraction": 0.0,
        "damping_ratio": 0.2,
        "joint_type": "pin",
    })
    send("assign_joint_type", {"clear": True, "ids": pads})

    problems = []
    counts = result.get("joint_type_counts") or {}
    if not counts.get("contact"):
        problems.append("the pad rule reached no joint at all")

    if not result.get("stable"):
        problems.append(
            f"unstable at {1000 * (result.get('max_pin_displacement_m') or 0.0):.1f} mm with "
            f"{result.get('contact_joints_open')} of {counts.get('contact')} contact joints "
            "open - a determinate truss puts only vertical reactions into its supports")

    return problems


def timber_mass(dx_mm: float, dy_mm: float, dz_mm: float, density: float) -> float:
    return density * (dx_mm / 1000.0) * (dy_mm / 1000.0) * (dz_mm / 1000.0)


def hybrid_masses(panel_reach: float) -> dict[str, float]:
    """Every body's mass, by geometry and density. The total is asserted against this."""
    pad = concrete_mass(*HYBRID_PAD)
    column = concrete_mass(*HYBRID_COLUMN)
    beam = timber_mass(
        HYBRID_SPAN_X + HYBRID_COLUMN[0], HYBRID_BEAM_WIDTH, HYBRID_BEAM_DEPTH,
        TIMBER_GLULAM_DENSITY)
    panel = timber_mass(
        HYBRID_SPAN_X / HYBRID_PANEL_COUNT, panel_reach, HYBRID_PANEL_THICKNESS,
        TIMBER_CLT_DENSITY)
    return {"pad": pad, "column": column, "beam": beam, "panel": panel}


def hybrid_total_weight_n(panel_reach: float, short_panels: int = 0) -> float:
    m = hybrid_masses(HYBRID_SPAN_Y)
    total = 4.0 * m["pad"] + 4.0 * m["column"] + 2.0 * m["beam"]
    full = HYBRID_PANEL_COUNT - short_panels
    total += full * m["panel"]
    if short_panels:
        total += short_panels * hybrid_masses(panel_reach)["panel"]

    return total * GRAVITY


def hybrid_build(short_panels: int = 0, panel_reach: float = 0.0) -> Callable[[], str]:
    """The frame, with `short_panels` of them reaching only `panel_reach` in y."""

    def build() -> str:
        m = hybrid_masses(HYBRID_SPAN_Y)
        lines = []
        half_pad = HYBRID_PAD[0] / 2.0
        half_col = HYBRID_COLUMN[0] / 2.0
        half_beam = HYBRID_BEAM_WIDTH / 2.0

        for xi, x in enumerate((0.0, HYBRID_SPAN_X)):
            for yi, y in enumerate((0.0, HYBRID_SPAN_Y)):
                lines.append(
                    f'world_box("PAD_{xi}{yi}", {x - half_pad!r}, {y - half_pad!r}, '
                    f'{-HYBRID_PAD[2]!r}, {x + half_pad!r}, {y + half_pad!r}, 0.0, '
                    f'{m["pad"]!r}, "CONCRETE")')
                lines.append(
                    f'world_box("COLUMN_{xi}{yi}", {x - half_col!r}, {y - half_col!r}, 0.0, '
                    f'{x + half_col!r}, {y + half_col!r}, {HYBRID_COLUMN_TOP!r}, '
                    f'{m["column"]!r}, "CONCRETE")')

        # One glulam beam along each frame line, running the full length over both columns.
        for yi, y in enumerate((0.0, HYBRID_SPAN_Y)):
            lines.append(
                f'world_box("BEAM_{yi}", {-half_col!r}, {y - half_beam!r}, '
                f'{HYBRID_COLUMN_TOP!r}, {HYBRID_SPAN_X + half_col!r}, {y + half_beam!r}, '
                f'{HYBRID_BEAM_TOP!r}, {m["beam"]!r}, "GLULAM")')

        # CLT panels spanning between the beams, bearing on the top of each.
        width = HYBRID_SPAN_X / HYBRID_PANEL_COUNT
        for i in range(HYBRID_PANEL_COUNT):
            short = i < short_panels
            reach = panel_reach if short else HYBRID_SPAN_Y
            mass = hybrid_masses(reach)["panel"] if short else m["panel"]
            lines.append(
                f'world_box("PANEL_{i}", {i * width!r}, 0.0, {HYBRID_BEAM_TOP!r}, '
                f'{(i + 1) * width!r}, {reach!r}, '
                f'{HYBRID_BEAM_TOP + HYBRID_PANEL_THICKNESS!r}, {mass!r}, "CLT")')

        return "\n".join(lines)

    return build


# What each pair of classes is, as an engineer would state it. These are the rules the cases
# vary; everything else about the model is held fixed.
HYBRID_RULES_AS_BUILT = [
    # State what is known; where the real detail sits between two types, take the weaker.
    #
    # A verdict from this evaluator is a lower bound or it is nothing. Overstating a
    # connection makes a structure look stiffer and more redundant than it is, and the failure
    # that hides is the one nobody sees coming. Understating it costs a sound structure being
    # reported as marginal, which is the error you can afford.
    #
    # Cast in one pour, so the pad and the column above it really are one moment connection.
    # That is knowledge, not optimism, so it is stated.
    ("CONCRETE", "CONCRETE", "welded"),
    # A glulam beam in a bolted shoe carries force and no moment. Also known.
    ("GLULAM", "CONCRETE", "pin"),
    # A CLT panel laid on a beam. Nothing holds it down.
    ("CLT", "GLULAM", "contact"),
    # Panel to panel is where the judgement is. A screwed spline transfers shear along the
    # joint and little moment, and none of the three types is that: welded keeps the whole
    # 4000 mm line but adds full moment continuity, while pin keeps no line at all - it
    # collapses the joint to one point, about which two panels simply hinge, so it is not a
    # weaker weld but a different thing. The spline is between them and neither is it. Taking
    # the weaker claims nothing of the screws, which is also what the deck has if they are
    # missed on site.
    ("CLT", "CLT", "contact"),
]

HYBRID_RULES_SPLINE_UPPER = [
    # The same frame with the spline claimed as a full moment connection along the panel
    # edge - the optimistic end of the bracket, kept because running both ends is how you see
    # whether a detail is load-bearing for the verdict. Where the two disagree, the lower
    # bound is the answer and the difference is what that detail is worth.
    ("CONCRETE", "CONCRETE", "welded"),
    ("GLULAM", "CONCRETE", "pin"),
    ("CLT", "GLULAM", "contact"),
    ("CLT", "CLT", "welded"),
]

HYBRID_RULES_DRY = [
    # Nothing claimed anywhere: every joint a bearing, including the cast base. The floor of
    # the bracket, and a real system in its own right - a dry-stacked frame.
    ("CONCRETE", "CONCRETE", "contact"),
    ("GLULAM", "CONCRETE", "contact"),
    ("CLT", "GLULAM", "contact"),
    ("CLT", "CLT", "contact"),
]

HYBRID_RULES_PINNED_BASE = [
    # As built, except the column is set on the pad rather than cast into it - the base
    # carries no moment, which is what turns a portal frame into a mechanism unless something
    # else braces it.
    ("CONCRETE", "CONCRETE", "pin"),
    ("GLULAM", "CONCRETE", "pin"),
    ("CLT", "GLULAM", "contact"),
    ("CLT", "CLT", "contact"),
]

# Every pair any of the sets above names, so a case can clear the table before stating its own
# and inherit nothing from whichever case ran before it.
HYBRID_ALL_PAIRS = sorted({
    (a, b)
    for rules in (HYBRID_RULES_AS_BUILT, HYBRID_RULES_SPLINE_UPPER, HYBRID_RULES_DRY,
                  HYBRID_RULES_PINNED_BASE)
    for a, b, _ in rules
})


def hybrid_check(rules, expect_stable: bool, weight_n: float, expect_types=None):
    """Apply one set of construction rules to the model, then evaluate it."""

    def check(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
        problems = []

        # Rules live in the document and outlast any one case, so each run states its own from
        # a clean table rather than inheriting whatever the last case left behind.
        send("assign_joint_type", {"prune": True})
        for a, b in HYBRID_ALL_PAIRS:
            send("assign_joint_type", {"clear": True, "layer": a, "with_layer": b})
        for a, b, joint in rules:
            send("assign_joint_type", {"joint_type": joint, "layer": a, "with_layer": b})

        # Drawn as it goes when asked. The graph overlay shows what the solver was handed -
        # which elements meet, and what each joint resolved to - and display leaves the
        # settled geometry on screen afterwards. Off by default because a run that draws is a
        # run someone has to be watching.
        if SHOW_WORK:
            send("graph_display", {"enabled": True, "ids": ids})

        result = send("evaluate_stability", {
            "mode": "pinned_dynamic",
            "ids": ids,
            "gravity": GRAVITY,
            "display": SHOW_WORK,
            "solver_substeps": 1,
            "lateral_load_fraction": 0.0,
        })
        if result.get("success") is not True:
            return [str(result.get("message"))]

        if bool(result.get("stable")) != expect_stable:
            problems.append(
                f"{result.get('verdict')} at {1000.0 * (result.get('max_pin_displacement_m') or 0.0):.1f} mm "
                f"against a {1000.0 * (result.get('mechanism_threshold_m') or 0.0):.1f} mm limit, "
                f"expected {'stable' if expect_stable else 'unstable'}")

        # Independent of the solver: the weight is geometry times density, done by hand.
        got = result.get("total_weight_n") or 0.0
        if abs(got - weight_n) > 0.01 * weight_n:
            problems.append(
                f"total weight {got:.0f} N, expected {weight_n:.0f} from geometry and density")

        if expect_types is not None:
            counts = result.get("joint_type_counts") or {}
            for name, want in expect_types.items():
                if counts.get(name) != want:
                    problems.append(
                        f"{name} joints {counts.get(name)}, expected {want} - "
                        f"the rules did not reach the joints they name")

        return problems

    return check


# --------------------------------------------------------------------------------------
# Pavilion: a podium, free-standing walls, and a roof slab laid on them
# --------------------------------------------------------------------------------------
#
# The Barcelona arrangement, reduced to what decides whether it stands: a slab on the ground,
# walls standing on it with nothing tying them down, and a slab laid on top of them. Nothing
# is fixed to anything - every joint is a dry bearing - so the composition of the walls in
# plan is the whole structure. Move the same four walls around and the answer changes.
#
# This is the case a point-pin model cannot have. A wall bearing on a slab is stiff in its own
# plane and soft across it, and that difference only exists because the joint is built over
# the measured bearing: a joint at a point has no lever arm and a 3 m wall then behaves like a
# 200 mm column. The pinwheel stands because its walls face two ways; the parallel version
# stands under its own weight and has nothing at all resisting sway across them.

PAVILION_PODIUM = (8000.0, 5000.0, 200.0)
PAVILION_WALL_THICKNESS = 200.0
PAVILION_WALL_HEIGHT = 2500.0
PAVILION_ROOF_THICKNESS = 200.0

# Each wall as its plan rectangle, (x0, y0, x1, y1), rather than a centre line and a
# thickness. Written as centre lines first, the four pinwheel walls overlapped 200 x 200 at
# every corner where one met another - solids sharing volume, which is not a joint but a
# modelling error, and the graph duly found contacts there that behaved like nothing real. The
# pinwheel then read unstable at 155 mm in 3772 steps while the parallel arrangement stood,
# which is the opposite of the physics. Stating the rectangles makes abutting explicit and the
# error impossible to write.
PAVILION_PINWHEEL = (
    (600.0, 900.0, 3400.0, 1100.0),      # along x, low - stops at both walls it meets
    (3400.0, 900.0, 3600.0, 4100.0),     # along y, right
    (3600.0, 3900.0, 7600.0, 4100.0),    # along x, high
    (400.0, 900.0, 600.0, 4100.0),       # along y, left
)

# The same four walls, all facing the same way, and none of them touching another. Every one
# is stiff along x and nothing resists anything across it.
PAVILION_PARALLEL = (
    (400.0, 600.0, 3600.0, 800.0),
    (4000.0, 1700.0, 7600.0, 1900.0),
    (400.0, 3100.0, 3600.0, 3300.0),
    (4000.0, 4200.0, 7600.0, 4400.0),
)


def pavilion_build(walls=PAVILION_PINWHEEL, roof_shift_x: float = 0.0) -> Callable[[], str]:
    """Podium, walls, roof. `roof_shift_x` slides the roof off its supports."""

    def build() -> str:
        top = PAVILION_WALL_HEIGHT
        lines = [
            f'world_box("PODIUM", 0.0, 0.0, {-PAVILION_PODIUM[2]!r}, '
            f'{PAVILION_PODIUM[0]!r}, {PAVILION_PODIUM[1]!r}, 0.0, '
            f'{concrete_mass(*PAVILION_PODIUM)!r}, "PODIUM")'
        ]

        for i, (x0, y0, x1, y1) in enumerate(walls):
            mass = concrete_mass(x1 - x0, y1 - y0, PAVILION_WALL_HEIGHT)
            lines.append(
                f'world_box("WALL_{i}", {x0!r}, {y0!r}, 0.0, {x1!r}, {y1!r}, {top!r}, '
                f'{mass!r}, "WALL")')

        lines.append(
            f'world_box("ROOF", {roof_shift_x!r}, 0.0, {top!r}, '
            f'{PAVILION_PODIUM[0] + roof_shift_x!r}, {PAVILION_PODIUM[1]!r}, '
            f'{top + PAVILION_ROOF_THICKNESS!r}, '
            f'{concrete_mass(PAVILION_PODIUM[0], PAVILION_PODIUM[1], PAVILION_ROOF_THICKNESS)!r}, '
            f'"ROOF")')

        return "\n".join(lines)

    return build


# Nothing is fixed to anything: the walls stand on the podium and the roof is laid on the
# walls. Stated as bearings rather than left to the default, because a default weld here would
# make the whole pavilion one welded box and answer a different question entirely.
PAVILION_RULES = [
    ("WALL", "PODIUM", "contact"),
    ("ROOF", "WALL", "contact"),
    ("WALL", "WALL", "contact"),
]

PAVILION_ALL_PAIRS = tuple((a, b) for a, b, _ in PAVILION_RULES)


def pavilion_check(expect_stable: bool, sway=None):
    """Apply the bearing rules, evaluate, and optionally check how it sways."""

    def check(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
        problems = []
        send("assign_joint_type", {"prune": True})
        for a, b in PAVILION_ALL_PAIRS:
            send("assign_joint_type", {"clear": True, "layer": a, "with_layer": b})
        for a, b, joint in PAVILION_RULES:
            send("assign_joint_type", {"joint_type": joint, "layer": a, "with_layer": b})

        if SHOW_WORK:
            send("graph_display", {"enabled": True, "ids": ids})

        # No imperfection, and that is a statement about this structure rather than a
        # convenience.
        #
        # The imperfection is applied as a velocity - stress-free, which is why it is applied
        # that way at all - and at a span/1000 offset it comes to 0.43 m/s here. On a truss
        # whose joints hold in tension that is a nudge which rings out. On a dry-stacked
        # pavilion it is a shove: friction has no way to put back what slides, so every body
        # keeps the ground it loses. With it the pinwheel read unstable at 50 mm; without it
        # nothing moves at all, 0.02 mm across the whole model. The verdict was measuring the
        # kick.
        #
        # What this arrangement should be asked instead is how it resists a load that is
        # actually a load, which is the notional lateral fraction below.
        result = send("evaluate_stability", {
            "mode": "pinned_dynamic",
            "ids": ids,
            "gravity": GRAVITY,
            "display": SHOW_WORK,
            "solver_substeps": 1,
            "imperfection_fraction": 0.0,
            "lateral_load_fraction": 0.05 if sway else 0.0,
        })
        if result.get("success") is not True:
            return [str(result.get("message"))]

        if bool(result.get("stable")) != expect_stable:
            problems.append(
                f"{result.get('verdict')} at "
                f"{1000.0 * (result.get('max_pin_displacement_m') or 0.0):.1f} mm against a "
                f"{1000.0 * (result.get('mechanism_threshold_m') or 0.0):.1f} mm limit, "
                f"expected {'stable' if expect_stable else 'unstable'}")

        if sway:
            low, high = sway
            block = result.get("sway") or {}
            kx = block.get("sway_stiffness_x_n_per_m")
            ky = block.get("sway_stiffness_y_n_per_m")
            if not kx or not ky:
                problems.append(f"no sway measured: {block}")
            else:
                ratio = max(kx, ky) / min(kx, ky)
                if not (low <= ratio <= high):
                    problems.append(
                        f"sway anisotropy {ratio:.1f} (x {kx:.3g}, y {ky:.3g}), "
                        f"expected {low}..{high}")

        return problems

    return check


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
# Welded, stated rather than inherited. These cases measure a *member's* axial stiffness
# against a closed form - W/3k for three columns - which assumes the joints between them add
# no compliance of their own. That is a welded idealisation, and it used to arrive by way of
# the evaluator's default. The default is contact now, and contact is not the same thing
# here: the two-storey stack reads 1.519 mm under it against 0.786 welded and 0.790 pinned,
# because the merged spacer node gives it a joint that can open. Which of those numbers is
# right is a question about the solver; what the case is asking is a question about EA/L, so
# it says which joint it means and asks its own question.
#
# The particle path cannot express a joint type at all, so this is inert there.
MICRO_PARAMS = {
    "lateral_load_fraction": 0.0,
    "imperfection_fraction": 0.0,
    "joint_type": "welded",
}


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
axis_box("DIAGONAL", (500.0,1400.0,0.0), (1700.0,1400.0,1200.0), {EXTENT_COLUMN_SIDE!r}, 54.0)
"""


# name -> (long side, short side, bearing of the long axis) from the drawn geometry. The
# angle is None where the footprint is square and its long axis is therefore arbitrary.
EXTENT_EXPECTED = {
    "WALL": (EXTENT_WALL_LENGTH, EXTENT_WALL_THICKNESS, 0.0),
    "WALL_ROT30": (EXTENT_WALL_LENGTH, EXTENT_WALL_THICKNESS, EXTENT_WALL_ROTATION_DEG),
    "COLUMN": (EXTENT_COLUMN_SIDE, EXTENT_COLUMN_SIDE, None),
}

# A square-cut member landing on a flat pad at 45 degrees touches it along one edge, and an
# edge is not a bearing: there is no plane to report and no direction for a contact joint to
# open along. Saying so is the point of listing it here.
#
# Fitting a plane through the sampled region instead reported 45 degrees - the average of the
# member's inclined end and the pad's top, a direction neither surface points in - and a
# contact joint built on it shed the vertical load those members carry and pushed them
# sideways. On a braced bridge whose diagonals land on its pads that walked the truss off its
# supports, 112 mm against a 61 mm limit, where the same model welded or pinned stood at half
# a millimetre. The normal now comes from the surfaces rather than from a fit.
EXTENT_NO_BEARING = ("DIAGONAL",)

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

    # And the other direction: what must NOT be measured. Every assertion above says a
    # rectangle is right, and none of them can fail on a joint that has no rectangle to
    # begin with - which is exactly how a 45 degree bearing plane survived until a bridge
    # walked off its supports.
    for entry in measured:
        pair = [names.get(entry["a"], ""), names.get(entry["b"], "")]
        for name in EXTENT_NO_BEARING:
            if name in pair:
                problems.append(
                    f"{name} reports a bearing extent, normal {entry.get('normal')} - it meets "
                    "the pad along one edge, and an edge has no bearing plane")

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

# --------------------------------------------------------------------------------------
# Three contact states, two geometry kinds
# --------------------------------------------------------------------------------------

# A slab on a wall, drawn six times: at a half-millimetre gap, exactly touching, and sunk
# 20 mm in, each as Breps and again as meshes. The footprint is 400 x 300 every time and
# the three states differ only in where the slab sits, so any measurement that reads them
# differently is reading the drawing rather than the bearing.
#
# The buried pair is the case the whole exercise is for. Sampling measures surface-to-surface
# distance, which falls to zero as two things touch and grows again once they overlap, so it
# reports nothing at all for either buried joint. Exact measurement puts the bearing on the
# mean plane of the two faces - 2490 for a slab sunk 20 mm into a wall topping out at 2500 -
# which is where the shared surface is.
# A slab tilted on a wall top, at angles either side of the 20-degree window inside which two
# faces count as parallel enough to bear on one another.
#
# The window is the claim being tested, so the case has to fail on both sides of it: an area
# up to 19 degrees, a line at 21 and 25. Two faces crossing at a steep angle share a line
# rather than an area, and reporting a rectangle there would be inventing one - but refusing
# to report anything would throw away a contact that is real and has a measurable length.
#
# It exists because a slab tilted 10 degrees - well inside the window - was being refused, and
# not by the parallel test. Each region's plane carried its origin at a corner, so the offset
# between two regions was measured between two unrelated corners and read as buried far too
# deep. Origins are at region centroids now. Nothing about the angles below would have caught
# that; the widths are what catch it, because a corner-origin measurement that happens to pass
# still lands the bearing in the wrong place.
# A column landing at 45 degrees on a pad, drawn twice: leaning on its own base edge, and the
# same rotation taken about the base centre, which drives it through the pad's top face.
#
# The pair is the point. Both are the same joint at the same angle, and they differ only in
# whether the drawing goes through itself - so they are what separates a contact from a
# modelling error, and the report has to separate them too. The first touches along a 400 mm
# line and shares no volume. The second buries half its base, and what it shares is a surface:
# exactly half of a 400 x 400 base, because rotating about the centre sinks half of it.
#
# The buried reading is an assumption and the weaker one is kept beside it. A socket carries
# load over the region buried; an edge landing on a pad rocks on the edge. Which is meant is
# not in the geometry, so both numbers are reported and the penetration depth says which
# drawing produced them.
SKEW_COLUMN = 400.0
SKEW_COLUMN_HEIGHT = 1000.0
SKEW_ANGLE_DEG = 45.0


def skew_socket_scene() -> str:
    return f"""
import math
import Rhino


def tilted_column(name, x, pivot_y, mass):
    brep = Rhino.Geometry.Box(
        Rhino.Geometry.Plane.WorldXY,
        Rhino.Geometry.Interval(x, x + {SKEW_COLUMN!r}),
        Rhino.Geometry.Interval(0.0, {SKEW_COLUMN!r}),
        Rhino.Geometry.Interval(0.0, {SKEW_COLUMN_HEIGHT!r})).ToBrep()
    brep.Transform(Rhino.Geometry.Transform.Rotation(
        math.radians({SKEW_ANGLE_DEG!r}),
        Rhino.Geometry.Vector3d.XAxis,
        Rhino.Geometry.Point3d(x + {SKEW_COLUMN / 2.0!r}, pivot_y, 0.0)))
    attrs = Rhino.DocObjects.ObjectAttributes()
    attrs.Name = name
    attrs.SetUserString('rhinomcp.stability.v1',
                        '{{"mass": %r, "mass_unit": "kg"}}' % mass)
    built.append(str(doc.Objects.AddBrep(brep, attrs)))


# Rotated about the base edge: it leans and touches, nothing shared.
world_box('RESTING_PAD', -400.0, -400.0, -200.0, {SKEW_COLUMN + 400.0!r}, {SKEW_COLUMN + 400.0!r}, 0.0, 2000.0)
tilted_column('RESTING_COL', 0.0, 0.0, 400.0)

# Rotated about the base centre: half the base swings below the pad's top face.
world_box('SUNK_PAD', 2600.0, -400.0, -200.0, {3000.0 + SKEW_COLUMN + 400.0!r}, {SKEW_COLUMN + 400.0!r}, 0.0, 2000.0)
tilted_column('SUNK_COL', 3000.0, {SKEW_COLUMN / 2.0!r}, 400.0)
"""


def check_skew_socket(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    """A line where they touch, the shared surface where they overlap."""
    graph = send("get_connectivity_graph", {"ids": ids})
    names = {node["i"]: node.get("name", "") for node in graph.get("n", [])}
    found = {}
    for entry in graph.get("contact_extent_exact") or []:
        pair = {names.get(entry["a"], ""), names.get(entry["b"], "")}
        for tag in ("RESTING", "SUNK"):
            if f"{tag}_COL" in pair:
                found[tag] = entry

    problems = []

    resting = found.get("RESTING")
    if resting is None:
        problems.append("resting: no contact, and it touches along its base edge")
    else:
        if resting.get("kind") != "line":
            problems.append(f"resting: reported as {resting.get('kind')}, not a line")
        if abs(resting["line_length"] - SKEW_COLUMN) > 0.5:
            problems.append(f"resting: line {resting['line_length']:.2f} against {SKEW_COLUMN:.1f}")
        if resting["penetration_depth"] > 0.5:
            problems.append(
                f"resting: penetration {resting['penetration_depth']:.2f}, and it shares no volume")

    sunk = found.get("SUNK")
    if sunk is None:
        problems.append("sunk: no contact, and half its base is inside the pad")
        return problems

    if sunk.get("kind") != "buried":
        problems.append(f"sunk: reported as {sunk.get('kind')}, not a buried surface")

    # Half the base, because the rotation was taken about the middle of it.
    want_area = SKEW_COLUMN * SKEW_COLUMN / 2.0
    if abs(sunk["polygon_area"] - want_area) > want_area * 0.01:
        problems.append(f"sunk: shared surface {sunk['polygon_area']:.0f} against {want_area:.0f}")

    # The base corner furthest from the pivot swings down by half the side times sin 45.
    want_depth = SKEW_COLUMN / 2.0 * math.sin(math.radians(SKEW_ANGLE_DEG))
    if abs(sunk["penetration_depth"] - want_depth) > 1.0:
        problems.append(
            f"sunk: penetration {sunk['penetration_depth']:.1f} against {want_depth:.1f}")

    # The weaker reading is not thrown away by reporting the stronger one.
    if abs(sunk["line_length"] - SKEW_COLUMN) > 0.5:
        problems.append(f"sunk: line {sunk['line_length']:.2f} against {SKEW_COLUMN:.1f}")

    return problems


BEARING_TILT_WALL = (400.0, 300.0)
BEARING_TILT_BURIAL = 20.0
BEARING_TILT_MEASURED = (0.0, 10.0, 15.0, 19.0)
BEARING_TILT_REFUSED = (21.0, 25.0)


def bearing_tilt_scene() -> str:
    lines = ["import math", "import Rhino", ""]
    dx, dy = BEARING_TILT_WALL
    top = 2500.0
    for index, tilt in enumerate(BEARING_TILT_MEASURED + BEARING_TILT_REFUSED):
        x = index * 2000.0
        lines.append(
            f"world_box('WALL_{int(tilt):02d}', {x!r}, {-dy / 2.0!r}, 0.0, {x + dx!r}, "
            f"{dy / 2.0!r}, {top!r}, 2000.0)")
        # The slab overhangs the wall on every side, so the bearing is the wall's own
        # footprint foreshortened onto the tilted plane and nothing else.
        lines.append(
            f"slab = Rhino.Geometry.Box(Rhino.Geometry.Plane.WorldXY,\n"
            f"    Rhino.Geometry.Interval({x - 100.0!r}, {x + dx + 100.0!r}),\n"
            f"    Rhino.Geometry.Interval({-dy / 2.0 - 100.0!r}, {dy / 2.0 + 100.0!r}),\n"
            f"    Rhino.Geometry.Interval({top - BEARING_TILT_BURIAL!r}, "
            f"{top - BEARING_TILT_BURIAL + 200.0!r})).ToBrep()")
        lines.append(
            f"slab.Transform(Rhino.Geometry.Transform.Rotation(math.radians({tilt!r}),\n"
            f"    Rhino.Geometry.Vector3d.XAxis,\n"
            f"    Rhino.Geometry.Point3d({x + dx / 2.0!r}, 0.0, "
            f"{top - BEARING_TILT_BURIAL + 100.0!r})))")
        lines.append("attrs = Rhino.DocObjects.ObjectAttributes()")
        lines.append(f"attrs.Name = 'SLAB_{int(tilt):02d}'")
        lines.append(
            "attrs.SetUserString('rhinomcp.stability.v1', "
            "'{\"mass\": 500.0, \"mass_unit\": \"kg\"}')")
        lines.append("built.append(str(doc.Objects.AddBrep(slab, attrs)))")
    return "\n".join(lines) + "\n"


# Half a millimetre on a 300 mm bearing. The widths below are closed forms, not fits.
BEARING_TILT_TOLERANCE_MM = 0.5


def check_bearing_tilt(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    """Foreshortened where the faces are near enough parallel, refused where they are not."""
    graph = send("get_connectivity_graph", {"ids": ids})
    names = {node["i"]: node.get("name", "") for node in graph.get("n", [])}
    measured = {}
    for entry in graph.get("contact_extent_exact") or []:
        pair = {names.get(entry["a"], ""), names.get(entry["b"], "")}
        tag = next(
            (t for t in BEARING_TILT_MEASURED + BEARING_TILT_REFUSED
             if f"WALL_{int(t):02d}" in pair),
            None)
        if tag is not None:
            measured[tag] = entry

    dx, dy = BEARING_TILT_WALL
    problems = []
    for tilt in BEARING_TILT_MEASURED:
        entry = measured.get(tilt)
        if entry is None:
            problems.append(f"{tilt:.0f} deg: no exact bearing, and it is inside the window")
            continue

        # The wall's footprint projected onto the mean plane of the pair: unchanged along the
        # tilt axis, foreshortened across it.
        want = sorted((dx, dy * math.cos(math.radians(tilt))), reverse=True)
        got = sorted((entry["length_u"], entry["length_v"]), reverse=True)
        for measure, expected, axis in zip(got, want, ("long", "short")):
            if abs(measure - expected) > BEARING_TILT_TOLERANCE_MM:
                problems.append(
                    f"{tilt:.0f} deg: {axis} side {measure:.2f} against {expected:.2f}")

    for tilt in BEARING_TILT_MEASURED:
        entry = measured.get(tilt)
        if entry is not None and entry.get("kind") != "planar":
            problems.append(f"{tilt:.0f} deg: reported as {entry.get('kind')}, not an area")

    for tilt in BEARING_TILT_REFUSED:
        entry = measured.get(tilt)
        if entry is None:
            problems.append(f"{tilt:.0f} deg: no contact at all, and the faces do cross")
            continue

        # Crossing faces that also overlap report the surface inside the volume they share;
        # crossing faces that merely touch report the line. Both keep the line's length, and
        # which one is reported follows from whether the drawing goes through itself.
        expected_kind = "buried" if entry["penetration_depth"] > 0.0 else "line"
        if entry.get("kind") != expected_kind:
            problems.append(
                f"{tilt:.0f} deg: reported as {entry.get('kind')}, expected {expected_kind} "
                f"at penetration {entry['penetration_depth']:.1f}")
            continue

        # The line runs along the wall's own width, clipped by whichever face is narrower.
        if abs(entry["line_length"] - dx) > BEARING_TILT_TOLERANCE_MM:
            problems.append(
                f"{tilt:.0f} deg: line {entry['line_length']:.2f} long against {dx:.1f}")

        if abs(entry["skew_deg"] - tilt) > 0.1:
            problems.append(f"{tilt:.0f} deg: skew reported as {entry['skew_deg']:.2f}")

        # The normal comes from the face being pressed into - the slab's underside - and not
        # from the bisector of the two face normals, which sits half the skew angle away and
        # points in a direction neither surface faces.
        if abs(entry["bisector_deg"] - tilt / 2.0) > 0.5:
            problems.append(
                f"{tilt:.0f} deg: normal sits {entry['bisector_deg']:.2f} from the bisector, "
                f"expected {tilt / 2.0:.2f}")

    return problems


BEARING_STATE_WALL = (400.0, 300.0)
BEARING_STATE_TOP = 2500.0
BEARING_STATE_BURIAL = 20.0

BEARING_STATES = (
    ("GAP", 0.5, False),
    ("TOUCH", 0.0, False),
    ("BURIED", -BEARING_STATE_BURIAL, False),
    ("MGAP", 0.5, True),
    ("MTOUCH", 0.0, True),
    ("MBURIED", -BEARING_STATE_BURIAL, True),
)


def bearing_states_scene() -> str:
    lines = [
        "import Rhino",
        "",
        "def kind_box(name, x0, y0, z0, x1, y1, z1, mass, as_mesh):",
        "    brep = Rhino.Geometry.Box(",
        "        Rhino.Geometry.Plane.WorldXY,",
        "        Rhino.Geometry.Interval(x0, x1),",
        "        Rhino.Geometry.Interval(y0, y1),",
        "        Rhino.Geometry.Interval(z0, z1)).ToBrep()",
        "    attrs = Rhino.DocObjects.ObjectAttributes()",
        "    attrs.Name = name",
        "    attrs.SetUserString('rhinomcp.stability.v1',",
        "                        '{\"mass\": %r, \"mass_unit\": \"kg\"}' % mass)",
        "    if as_mesh:",
        "        mesh = Rhino.Geometry.Mesh()",
        "        for part in Rhino.Geometry.Mesh.CreateFromBrep(",
        "                brep, Rhino.Geometry.MeshingParameters.FastRenderMesh):",
        "            mesh.Append(part)",
        "        built.append(str(doc.Objects.AddMesh(mesh, attrs)))",
        "    else:",
        "        built.append(str(doc.Objects.AddBrep(brep, attrs)))",
        "",
    ]
    dx, dy = BEARING_STATE_WALL
    for index, (tag, offset, as_mesh) in enumerate(BEARING_STATES):
        # Well apart in plan, so the only contact each pair has is its own.
        x = index * 2000.0
        top = BEARING_STATE_TOP
        lines.append(
            f"kind_box('WALL_{tag}', {x!r}, {-dy / 2.0!r}, 0.0, {x + dx!r}, {dy / 2.0!r}, "
            f"{top!r}, 2000.0, {as_mesh!r})")
        lines.append(
            f"kind_box('SLAB_{tag}', {x - 100.0!r}, {-dy / 2.0 - 100.0!r}, {top + offset!r}, "
            f"{x + dx + 100.0!r}, {dy / 2.0 + 100.0!r}, {top + offset + 200.0!r}, 500.0, "
            f"{as_mesh!r})")
    return "\n".join(lines) + "\n"


# A tenth of a millimetre on a 400 mm bearing. The claim is exactness, so the tolerance is
# the reporting precision rather than a fitting allowance.
BEARING_STATE_TOLERANCE_MM = 0.1


def check_bearing_states(send: Callable[[str, dict], Any], ids: list[str]) -> list[str]:
    """The same footprint, whatever state and whatever kind it was drawn in."""
    graph = send("get_connectivity_graph", {"ids": ids})
    names = {node["i"]: node.get("name", "") for node in graph.get("n", [])}
    measured = {}
    for entry in graph.get("contact_extent_exact") or []:
        pair = {names.get(entry["a"], ""), names.get(entry["b"], "")}
        tag = next((t for t, _, _ in BEARING_STATES if f"WALL_{t}" in pair), None)
        if tag is not None:
            measured[tag] = entry

    dx, dy = BEARING_STATE_WALL
    expected = sorted((dx, dy), reverse=True)

    problems = []
    for tag, offset, _ in BEARING_STATES:
        entry = measured.get(tag)
        if entry is None:
            problems.append(f"{tag}: no exact bearing measured")
            continue

        sides = sorted((entry["length_u"], entry["length_v"]), reverse=True)
        for got, want, axis in zip(sides, expected, ("long", "short")):
            if abs(got - want) > BEARING_STATE_TOLERANCE_MM:
                problems.append(f"{tag}: {axis} side {got:.2f} against {want:.1f}")

        # The bearing sits on the mean plane of the two faces, so burying the slab 20 mm
        # moves it down 10 - not to the wall top, and not to the slab underside.
        want_z = BEARING_STATE_TOP + offset / 2.0
        if abs(entry["centre"][2] - want_z) > BEARING_STATE_TOLERANCE_MM:
            problems.append(f"{tag}: bearing at z {entry['centre'][2]:.2f} against {want_z:.2f}")

        want_penetration = max(0.0, -offset)
        if abs(entry["penetration_depth"] - want_penetration) > BEARING_STATE_TOLERANCE_MM:
            problems.append(
                f"{tag}: penetration {entry['penetration_depth']:.2f} against "
                f"{want_penetration:.1f}")

    return problems


FAST = "fast"
SLOW = "slow"

# A third tier, asking a different question from either. Fast and slow both ask whether the
# verdict is right; micro asks whether a number is right, against a closed form. It is the
# tier that can say an integrator is wrong rather than merely disagreeing with the other
# one, so it is what decides which of the two survives.
MICRO = "micro"


GEOMETRY = "geometry"

# A fifth tier, for trying structural systems against each other rather than for guarding a
# number or a verdict already known.
#
# Every other tier asks whether the evaluator is right about a case whose answer was
# established by hand. This one asks the question the evaluator exists to answer: given one
# geometry, which ways of building it stand up. The cases share a model and differ only in
# what its connections are, so the comparison between them is the result - a first guess at
# whether a system works, which is what this is for.
#
# It is also the answer to the coverage risk the plan named. Fifteen cases, mostly one bridge
# and one stack, and four separate defects were each found the first time a case varied
# something previously held fixed. A hybrid of concrete and mass timber varies almost
# everything at once: two materials five times apart in density, three joint types in one
# model, and panels that bear rather than connect.
SYSTEMS = "systems"


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
        name="bearing_states_and_kinds",
        mode="none",
        tier=GEOMETRY,
        stable=True,
        reason=(
            "one 400 x 300 bearing drawn six ways - gapped, touching and buried 20 mm, as "
            "Breps and as meshes; sampling reports nothing at all for either buried pair"),
        build=bearing_states_scene,
        check=check_bearing_states,
    ),
    Case(
        name="bearing_tilted_faces",
        mode="none",
        tier=GEOMETRY,
        stable=True,
        reason=(
            "a slab tilted on a wall top at 0, 10, 15 and 19 degrees measures the wall's "
            "footprint foreshortened; at 21 and 25 the faces cross and no bearing is claimed"),
        build=bearing_tilt_scene,
        check=check_bearing_tilt,
    ),
    Case(
        name="bearing_skew_socket",
        mode="none",
        tier=GEOMETRY,
        stable=True,
        reason=(
            "a column at 45 degrees resting on its base edge touches along a line and shares "
            "nothing; the same rotation about the base centre buries half of it, and half a "
            "base is what the shared surface measures"),
        build=skew_socket_scene,
        check=check_skew_socket,
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
    # A concrete frame carrying a mass-timber deck, built as it would be built.
    Case(
        name="hybrid_as_built",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "pads cast into columns, beams pinned on the column heads, CLT panels bearing "
            "100 mm on each beam - every panel's centre of mass is between its two bearings"),
        build=hybrid_build(),
        check=hybrid_check(HYBRID_RULES_AS_BUILT, True, hybrid_total_weight_n(HYBRID_SPAN_Y)),
    ),
    # The same frame with one panel too short to reach the far beam. It then bears 100 mm on
    # one beam with its centre of mass 1400 mm past that bearing, which is the stair and the
    # pedestal again in a different material: a bearing carries no tension, so it rotates off.
    Case(
        name="hybrid_panel_off_bearing",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=False,
        reason=(
            "one CLT panel reaches 3800 of the 4000 mm span, so it bears on one beam only "
            "with its centre of mass 1800 mm beyond that bearing, and unsplined panels "
            "cannot hold it up - they meet on a vertical face"),
        build=hybrid_build(short_panels=1, panel_reach=HYBRID_SHORT_PANEL),
        check=hybrid_check(
            HYBRID_RULES_AS_BUILT, False, hybrid_total_weight_n(HYBRID_SHORT_PANEL, short_panels=1)),
    ),
    # The same defective panel with its spline claimed as a moment connection: the optimistic
    # end of the bracket. Its neighbours are sound and now carry it, so the answer flips.
    #
    # This is what running both ends is for. The verdict here rests entirely on a line of
    # screws being as good as continuous timber, and the pair says so out loud: the lower
    # bound is the answer, and the distance between the two is what that detail is worth. A
    # single run at the optimistic end would have reported a sound deck and shown nothing.
    Case(
        name="hybrid_panel_spline_upper_bound",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "the same panel off its bearing, with the spline claimed as full moment "
            "continuity along the panel edge - an upper bound, and the lower bound "
            "disagrees, which is the finding"),
        build=hybrid_build(short_panels=1, panel_reach=HYBRID_SHORT_PANEL),
        check=hybrid_check(
            HYBRID_RULES_SPLINE_UPPER, True,
            hybrid_total_weight_n(HYBRID_SHORT_PANEL, short_panels=1)),
    ),
    # The floor of the bracket: nothing claimed anywhere, every joint a bearing. It stands,
    # and the reason is worth having a case for.
    #
    # A dry bearing is not a hinge. A 700 mm pad under a 400 mm column resists rotation with
    # k d^2 over its own width for as long as it stays in compression, so a frame with nothing
    # but bearings has real moment capacity at every joint - measured, its sway stiffness is
    # 1.44e8 N/m against the as-built frame's 1.45e8, which is no difference at all. What it
    # lacks is any capacity once a bearing opens, and that is a question about the load, not
    # about the frame standing under its own weight.
    #
    # This case asserted unstable and passed until the imperfection was turned off, at which
    # point it stood without moving at all. The verdict had been the kick. The physics in the
    # reason was wrong too - a four-hinge mechanism needs hinges, and there were none.
    Case(
        name="hybrid_dry_stacked",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "every joint a bearing, which carries moment over its own width while it is in "
            "compression - so a dry frame stands under its own weight, and sways no more "
            "than the as-built one"),
        build=hybrid_build(),
        check=hybrid_check(HYBRID_RULES_DRY, True, hybrid_total_weight_n(HYBRID_SPAN_Y)),
    ),
    # The one detail that decides this frame: whether the column is cast into its pad. Set on
    # it instead, the base carries no moment, and pinned at base and head each frame is the
    # same four-hinge mechanism. Everything else is as built.
    Case(
        name="hybrid_pinned_base",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=False,
        reason=(
            "columns set on their pads rather than cast in, so base and head are both real "
            "hinges and each frame is a mechanism - and note it is the *pinned* frame that "
            "goes, not the dry one: a pin discards the bearing, a contact keeps it"),
        build=hybrid_build(),
        check=hybrid_check(HYBRID_RULES_PINNED_BASE, False, hybrid_total_weight_n(HYBRID_SPAN_Y)),
    ),
    # Four walls facing two ways. Nothing is fixed to anything, so what holds the roof still
    # is the arrangement of the walls under it.
    Case(
        name="pavilion_pinwheel",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "walls in a pinwheel present a plane in both directions, so the roof is braced "
            "each way and the two sway stiffnesses are within a factor of three"),
        build=pavilion_build(PAVILION_PINWHEEL),
        check=pavilion_check(True, sway=(1.0, 3.0)),
    ),
    # The same four walls, the same roof, all facing one way. It still stands under its own
    # weight - nothing asks it not to - and has essentially nothing resisting sway across the
    # walls. A wall is stiff in its own plane and soft across it, and this is that fact at the
    # scale of a building rather than of a joint.
    #
    # It is also a case a point-pin model cannot have. Every joint there is a point, a point
    # has no lever arm, and the two directions would come out the same.
    Case(
        name="pavilion_parallel_walls",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "parallel walls brace one direction only: about 1.5e10 N/m along them against "
            "2e6 across, four orders apart, where the pinwheel is within a factor of three"),
        build=pavilion_build(PAVILION_PARALLEL),
        check=pavilion_check(True, sway=(100.0, 1.0e6)),
    ),
    # The roof slid 4 m off the walls that carry it. Its centre of mass then sits outside
    # everything holding it up, and a bearing carries no tension, so it goes.
    Case(
        name="pavilion_roof_off_walls",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=False,
        reason=(
            "the roof is displaced 4 m in x, putting its centre of mass beyond the walls "
            "under it, and nothing holds a dry bearing down"),
        build=pavilion_build(PAVILION_PINWHEEL, roof_shift_x=4000.0),
        check=pavilion_check(False),
    ),
    Case(
        name="joint_type_rules",
        mode="pinned_dynamic",
        tier=FAST,
        stable=True,
        reason=(
            "pair rule beats element rule beats default, and the resolved type reaches the "
            "solver: every state with a pin in it is a mechanism, the welded states stand"),
        build=rule_stair_build,
        check=check_joint_type_rules,
    ),
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
        # A truss is bolted at its nodes, so it says so. It used to rely on the evaluator's
        # default, which was welded; the default is contact now, and a truss whose members
        # merely touch is not a truss - its diagonals cannot pull, so the deck hangs off
        # nothing and sags 41.6 mm against a hand figure of 1.8. Stating the joint is not a
        # workaround for that: the members really are connected, and the hand figure this is
        # measured against is pinned-truss statics.
        params={"joint_type": "pin"},
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
        #
        # Measured on the particle path, and named as such now that the rigid path is the
        # default. These bounds are a calibration, not a verdict, and the two integrators
        # disagree about sway with nothing establishing which is right - so they are asserted
        # where they were measured. This bridge additionally reports no sway at all on the
        # rigid path: it does not settle inside the half-second run, and sway is only measured
        # after settling.
        params={"lateral_load_fraction": 0.05, "integrator": "particles"},
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
        # Measured on the particle path, and named as such now that the rigid path is the
        # default. These bounds are a calibration, not a verdict: the two integrators disagree
        # about sway - the braced bridge reads 9.0e8 on the rigid path against 1.13e9 here -
        # and nothing has established which is right. Re-baselining them onto the new default
        # would assert numbers nobody has checked; asserting them where they were measured
        # keeps them meaningful and leaves the disagreement on the record rather than
        # papering over it.
        #
        # The unbraced bridge additionally has no sway to report on the rigid path at all: it
        # does not settle inside the half-second run, and sway is only measured after settling.
        params={"lateral_load_fraction": 0.05, "integrator": "particles"},
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
        name="joint_capacity_binds",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "a cantilever arm needs 24.5 kN at one bearing point; 50 kN a point changes "
            "nothing and 2.5 kN a point lets the joint go"),
        build=capacity_scene,
        check=check_capacity,
    ),
    Case(
        name="joint_forces_reactions",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "three columns under an off-centre block carry W/3.2, W/3.2 and 1.2 W/3.2 by "
            "statics, and every one of them is in compression"),
        build=micro_stack_build(1),
        check=check_joint_forces,
    ),
    Case(
        name="bridge_on_pads",
        mode="pinned_dynamic",
        tier=SYSTEMS,
        stable=True,
        reason=(
            "a determinate truss puts vertical reactions into its supports and no thrust, so "
            "bolted to itself and set down on its pads it stands"),
        build=bridge_build(braced=True),
        check=check_bridge_on_pads,
    ),
    Case(
        name="bridge_unbraced_pinned_alias",
        mode="pinned",
        tier=FAST,
        # It stands, and it takes a second to say so.
        #
        # Four infinitesimal mechanisms stiffen at second order, so the structure is soft in
        # the direction the modes move and arrests rather than collapsing: 6.4 mm against a
        # collapse threshold of 60.8. But soft means slow. Over the default half second this
        # mode has not completed a single swing, and the test that says a motion is bounded
        # needs it to reverse twice - so the run ends inconclusive, and inconclusive reports
        # as not stable.
        #
        # A second is enough, and asking for one costs nothing: the run stops as soon as it
        # can conclude, so 1, 2 and 4 seconds all end at the same 42 samples with the same
        # 6.362 mm. Duration is a cap, not a price.
        #
        # It read unstable for a second reason too, now fixed in the solver: the verdict was
        # computed from 32 samples however long the run was, so the answer was not even
        # monotonic in duration - 3.0 mm and inconclusive over half a second, 10.8 and stable
        # over two, 5.1 and inconclusive over five, 5.1 and stable over ten. One trajectory,
        # four answers, differing only in how much of it anyone looked at.
        # It also passed for years on the evaluator's welded default, which removes the
        # mechanisms altogether - so the thing the case exists to test was never being tested.
        # Changing the default to contact is what exposed that, and stating the joint it is
        # actually about, pin, is what let the question be asked at all.
        stable=True,
        reason=(
            "infinitesimal mechanisms stiffen at second order, so it stands; and \"pinned\" "
            "must now answer identically to \"pinned_dynamic\""),
        build=bridge_build(braced=False),
        params={"joint_type": "pin", "duration_seconds": 1.0},
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
        # Was committed failing on the rigid-body integrator at 0.785 mm against 0.928, for
        # about as long as the rigid path has existed. It reads 0.942 now, and the fix is the
        # second of the two this comment used to name.
        #
        # The spacer between the storeys is 200 mm thick, and the clustering radius was the
        # body's own smallest dimension - so its top and bottom faces sat at exactly that
        # radius and merged. The middle came back as three bodies meeting at one point 100 mm
        # from where either face is, instead of two nodes with the spacer between them, and
        # removing a joint from the load path removed a spring from the series and stiffened
        # the whole stack.
        #
        # No threshold could have separated those two cases, because for a plate the right
        # answer and the wrong answer are the same number: its two faces are exactly its
        # smallest dimension apart. The radius rule assumes slenderness - a truss member's
        # ends are 2000 mm apart with a 150 mm section, which is decisive - and a plate has no
        # such gap by construction. What separates them is not how far apart two contacts are
        # but which side of the body each is on: with the body's own middle between them they
        # are on opposite faces, and opposite faces are two joints however close together.
        #
        # The first fix that comment named is still open and still worth doing: joint
        # stiffness is fixed at 2k per end rather than shared along a member's load path, so a
        # member's stiffness depends on how many joints it happens to have, which is a
        # property of the mesh rather than of the member. That no longer shows up here, but it
        # has not gone away.
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
