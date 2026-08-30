"""Extended Rhino MCP tools for selection, layers, materials, and view capture."""
import base64
import json
from typing import Any

from mcp.server.fastmcp import Image
from rhinomcp.server import mcp


@mcp.tool()
async def evaluate_stability(
    mode: str | None = None,
    current_step: int | None = None,
    joint_penetration: float | None = None,
    ground_settlement: float | None = None,
    duration_seconds: float | None = None,
    damping_ratio: float | None = None,
    integrator: str | None = None,
    joint_type: str | None = None,
    bearing_source: str | None = None,
    lateral_load_fraction: float | None = None,
    joint_stiffness_n_per_m: float | None = None,
    stability_threshold: float | None = None,
    rigid_strength: float | None = None,
    ground_support_stiffness_n_per_m: float | None = None,
    floor_z: float | None = None,
    gravity: float = 9.80665,
    solver_substeps: int = 1,
    display: bool = False,
    detail: str = "summary",
    graph: str | dict[str, Any] | None = None,
    layer: str | list[str] | None = None,
    ids: list[str] | None = None,
    bbox: list[list[float]] | None = None,
    bbox_mode: str = "intersects",
    selected: bool = False,
) -> dict[str, Any]:
    """Experimentally evaluate whether the active model stands up under gravity.

    The model must have a connectivity graph and positive mass assigned to
    every graph node. By default the graph is read from the active document.

    Two questions, chosen by `mode`, and neither subsumes the other, so run
    both: does the scope tip over as a whole ("assembly"), and is it a
    mechanism - can any element rotate or slide off its supports ("elements").

    Args:
        mode: "assembly" (default) fuses the whole scope into one rigid body
            with no joints at all, and answers whether it tips or slides.
            "elements" gives every element its own rigid body, joined at the
            connectivity graph's contact points by whatever joint type the
            model states there, and answers whether the assembly is a
            mechanism. What a joint carries is not part of this choice - it is
            per joint, set by assign_joint_type, defaulted by `joint_type`.
            "elements" reports per-element displacement and rotation and names
            the element that moved furthest; its result carries no floor
            strength, assembly transform or support margin.
        joint_penetration: How far a bearing surface may close under its own
            load, in document units. Sizes the joint stiffness where none is
            stated.
        ground_settlement: Assembly mode only. How far the assembly
            may settle into the ground under its own weight, in document units;
            the floor spring is sized from it where no floor strength is given.
            The multi-body modes size their ground springs from the joints
            instead and do not read it.
        joint_stiffness_n_per_m: Elements mode only. Axial joint stiffness in N/m,
            stated rather than derived, and applied to EVERY member in the scope.
            That last part matters: left unset, each member gets its own
            (E/rho) m / L^2 with the section recovered from mass, so a slender
            column comes out soft and the slab or pad it bears on comes out
            hundreds of times stiffer. State one value and they all become
            equally soft, which adds the pads' and slabs' compliance to the load
            path and can deflect several times more than the columns alone would.
            Use it when the connection governs and is much softer than anything
            it joins - a screwed or doweled timber joint, where the timber's own
            stiffness is nearly irrelevant and a joint test gives this number
            directly. Per-joint values are not supported yet.
        lateral_load_fraction: Elements mode only. The sideways probe that
            measures sway stiffness, as a fraction of the weight carried.
            **Off by default** - pass 0.05 to ask for it.

            It is off because measuring sway means settling the assembly and
            settling it again under load along each horizontal axis: three
            further integrations on top of the verdict run, six times the cost.
            A 47-member bridge takes 14.3 s with it and 2.3 s without, and the
            verdict is identical. Nothing is lost from the answer, only from
            the report.

            Ask for it once a candidate survives screening, because it answers
            a question the verdict cannot: whether a structure is braced or
            merely has not fallen yet. Four of the test bridge's modes are
            infinitesimal mechanisms that stand under their own weight, and the
            sway figure is the only thing separating that bridge from a
            properly braced one. 0.05 is the value every figure in these notes
            was measured at - the codes' few-parts-per-thousand notional force
            moves this model less than its own settling residual, so a smaller
            probe reads the residual rather than the structure.
        current_step: Number of solver steps to run. When omitted, Rhino uses a
            budget large enough for a collapse to develop; a short run makes a
            toppling assembly look stationary and so reads as stable. The run
            exits early once motion and rotation have both settled, and also
            once both are clearly diverging, since a collapse under way cannot
            reverse. Read solver_steps_run alongside rotation_deg: a run that
            exited on divergence reports the rotation reached at that moment,
            not at the end of the budget.
        stability_threshold: Maximum displacement considered stable, in the
            active Rhino document's length unit. When omitted, Rhino converts
            the canonical 0.01 m default to document units.
        rigid_strength: Assembly mode only: how rigid the single assembly body is.
            It no longer reaches the element joints - use joint_stiffness_n_per_m
            for those. When omitted, Rhino sizes it above the ground stiffness. The two goals are blended by
            weight, so a rigid strength below the floor lets the floor deform
            the assembly it is supporting and a sound structure reads as
            unstable. Pass a value only to study a deliberately compliant body.
        ground_support_stiffness_n_per_m: How stiffly the ground holds a standing
            vertex, in N/m of its tributary area. When omitted, Rhino sizes it
            from the assembly's total mass so that settling stays within a tenth
            of the stability threshold; a sound structure would otherwise read as
            unstable purely from sinking. Multiplied by each standing vertex's
            share of the footprint, so it is a subgrade modulus in Pa/m and the
            reported ground_bearing_area_m2 is the footprint. Accepted as
            floor_strength under its old name.
        floor_z: World Z elevation of the collision floor, in document units.
            When omitted, Rhino places the floor at the underside of the scoped
            assembly. Pass a value only to hold the floor at a fixed level: a
            scope that leaves out the pads its columns stand on would otherwise
            start in mid-air and spend the run falling to world zero.
        gravity: Downward gravitational acceleration in m/s².
        detail: "summary" (default) returns the verdict, every scalar, and a
            digest of the per-joint tables - the five most-tensioned joints, the
            five most-loaded, anything at capacity, the ground reactions - in a
            few KB. "full" returns every per-joint, per-node and per-step
            record as well, which on a 100-element assembly is over 100 KB and
            more than a tool result can carry into context. The full report is
            stored on the document either way; get_stability_report pages any
            section of it afterwards.
        duration_seconds: Elements mode only. How long to simulate, in seconds.
            Unlike a step count this means the same thing on every model: a mechanism
            with a tenth of gravity available to it covers 50 mm in 0.32 s, so the
            default 0.5 s separates falling from standing with room to spare.
        damping_ratio: Elements mode only. Viscous damping as a fraction of
            critical. Defaults to 0.02, the low end of what codes assume for steel
            framing. With none at all a sound structure oscillates forever about its
            static deflection and peaks at twice it. The two integrators do not mean
            the same thing by it: "particles" damps each particle against its own local
            stiffness, which over-damps the slow global mode, while "rigid_bodies"
            damps each joint against relative motion there, which barely touches a mode
            where both ends of a joint move together. The rigid path typically needs
            0.2 to settle inside a half-second run.
        bearing_source: Elements mode + integrator="rigid_bodies" only. Which
            measurement of a bearing the solver builds joints over. A joint's moment
            capacity is its bearing's size and nothing else, so this is not a
            reporting choice.

            "sampled" (default) - a grid walked across both surfaces, keeping points
            that come within a millimetre of the other. Approximate, and the extent it
            reports depends on where the grid happened to land: four identical
            400 x 400 joints measured 453, 536, 542 and 544. Two members meeting end to
            end give a rectangle of zero width, so a full bearing is solved as a hinge.
            Two bodies drawn overlapping give nothing at all.
            "exact" - the polygon the two bodies' flat faces actually share, on the mean
            plane between them. Covers touching, nearly touching and overlapping alike.
            Faces that cross rather than bear report the line they cross along, which
            carries force and no moment about itself - a body resting on an edge rocks.
            "buried" - as "exact", and additionally admits the surface inside the volume
            two bodies share. That area grows with how far the drawing goes through
            itself, so it credits a joint with moment capacity in proportion to a
            modelling artefact. Right where an overlap is a deliberate socket; wrong for
            truss members that merely interpenetrate at their nodes.
        joint_type: Elements mode + integrator="rigid_bodies" only. What every joint
            in the assembly is, since geometry cannot tell you: a screwed panel and a
            dry-stacked one look identical to an intersection test.

            "fixed" - the bearing carries force and moment, always. A moment
            connection: beam to column, a plate welded or bolted rigid. Not to be
            confused with mode="assembly", which is the whole scope as one rigid body.
            "pin" - the bearing collapses to its centre, so it carries force in three
            directions and no moment about any axis. Truss to truss, a single bolt.
            "contact" (default) - the bearing pushes and does not pull, with friction across it,
            so it opens as load leaves it. Dry masonry, a beam sitting on a corbel, a
            precast panel bearing on a pad.

            "contact" is the honest floor and "fixed" the optimistic ceiling, so
            running both brackets a verdict. Where a joint has no measured bearing
            region - it was found by proximity rather than by two faces meeting -
            "contact" has no direction to open along and falls back to fixed; the
            result reports how many were actually sided.

        integrator: Elements mode only. "rigid_bodies" (default) or
            "particles". They answer differently and the difference is not small.

            "rigid_bodies" integrates the body itself under F = ma and Euler's
            equations. It falls correctly, to one part in ten thousand; it builds each
            joint over the bearing region the graph measured, so a 1150 mm wall comes
            out 14 times stiffer in its own plane than across it; and it is the only
            path that can express a joint type at all. Its damping_ratio defaults to
            0.2, which is its own number and not the particle path's.

            "particles" holds each body's points to a fitted frame. It reproduces a
            closed-form axial deflection to about 1% and is the reference the micro
            tier is calibrated against, but it cannot represent free motion - a body
            with nothing under it falls at 0.2% of g, so an unstable verdict can only
            come from deformation crossing a limit, never from something toppling or
            dropping.

            It also cannot represent a joint type. A body there is particles held to a
            fitted frame by a goal that takes ONE strength for all of them, and a joint
            is a shared particle rather than a spring: one point has no lever arm, so
            it is a pin by construction. Welded has nowhere to put its moment and a
            shared particle can never open, so contact cannot happen either. Any
            joint_type argument and every rule assign_joint_type wrote are silently
            ignored on this path.

            Use "particles" to reproduce a calibrated deflection or to cross-check.
            Use the default for anything else.
        solver_substeps: Kangaroo substeps per solver step. Ignored by elements mode,
            which derives its own timestep from the stiffest spring holding the
            lightest mass.
        display: Cache evaluated geometry in Rhino for display when true.
        graph: Optional connectivity graph JSON. Overrides every other source.
        layer: Layer name, or list of names, to evaluate.
        ids: Object GUIDs to evaluate.
        bbox: World box filter [[min_x,min_y,min_z],[max_x,max_y,max_z]].
        bbox_mode: "intersects" (default), "contains_center", or "contained".
        selected: When True, evaluate the current Rhino selection.

    Scope the evaluation with layer/ids/bbox/selected whenever the document holds
    more than the assembly under test. Scoping does two things: it welds only the
    parts you name, instead of every object in the file, and it recomputes the
    graph on the spot. With no scope and no explicit graph the stored
    document-text graph is used, which is only rewritten on an unscoped
    get_connectivity_graph call and so can lag the model badly.

    A scoped request fails rather than guessing if the scope matches nothing, or
    if the graph would be truncated.
    """
    from rhinomcp.server import get_rhino_connection

    params: dict[str, Any] = {
        "gravity": gravity,
        "solver_substeps": solver_substeps,
        "display": display,
        "detail": detail,
    }
    if stability_threshold is not None:
        params["stability_threshold"] = stability_threshold
    if floor_z is not None:
        params["floor_z"] = floor_z
    if ground_support_stiffness_n_per_m is not None:
        params["ground_support_stiffness_n_per_m"] = ground_support_stiffness_n_per_m
    if rigid_strength is not None:
        params["rigid_strength"] = rigid_strength
    if mode is not None:
        params["mode"] = mode
    if joint_penetration is not None:
        params["joint_penetration"] = joint_penetration
    if ground_settlement is not None:
        params["ground_settlement"] = ground_settlement
    if duration_seconds is not None:
        params["duration_seconds"] = duration_seconds
    if damping_ratio is not None:
        params["damping_ratio"] = damping_ratio
    if integrator is not None:
        params["integrator"] = integrator
    if joint_type is not None:
        params["joint_type"] = joint_type
    if bearing_source is not None:
        params["bearing_source"] = bearing_source
    if lateral_load_fraction is not None:
        params["lateral_load_fraction"] = lateral_load_fraction
    if joint_stiffness_n_per_m is not None:
        params["joint_stiffness_n_per_m"] = joint_stiffness_n_per_m
    if current_step is not None:
        params["current_step"] = current_step
    if graph is not None:
        params["graph"] = json.dumps(graph, separators=(",", ":")) if isinstance(graph, dict) else graph
    if layer is not None:
        params["layer"] = layer
    if ids:
        params["ids"] = ids
    if bbox is not None:
        params["bbox"] = bbox
        params["bbox_mode"] = bbox_mode
    if selected:
        params["selected"] = True

    rhino = get_rhino_connection()
    return rhino.send_command("evaluate_stability", params)


@mcp.tool()
async def get_stability_report(
    section: str | None = None,
    sort: str | None = None,
    ascending: bool = False,
    limit: int = 20,
    offset: int = 0,
    ids: list[str] | None = None,
    joint_type: str | None = None,
    min_tension_n: float | None = None,
    reached_capacity_only: bool = False,
) -> dict[str, Any]:
    """Read one section of the last evaluate_stability report, a page at a time.

    evaluate_stability stores its complete report on the document and returns
    a summary. This reads the rest without running the solver again.

    Args:
        section: Which part to read. Omit to list the sections and their
            sizes. The pageable ones are "joint_forces" (one record per body
            per joint: force, tension, shear, capacity, which elements it
            joins), "nodes" (joint clusters: members, diameter, resolved type),
            "ground_sites" (each ground bearing point and the vertical reaction
            it carries), "bodies" (per-element displacement on the particle
            path), and the per-step traces. Any scalar or object section - say
            "sway" - is returned whole.
        sort: Field to order by. Defaults per section: tension_n for
            joint_forces, diameter_m for nodes, fz_n for ground_sites.
        ascending: Reverse the order. Default is largest first.
        limit: Records per page, 1-500. Default 20.
        offset: Records to skip, for the next page.
        ids: Keep only records whose guid, or whose members, are among these
            object ids.
        joint_type: Keep only "contact", "pin" or "fixed" joints.
        min_tension_n: Keep only joints carrying at least this much tension.
        reached_capacity_only: Keep only joints that yielded.

    Returns the page with total, matched and returned counts, so the caller
    can tell whether there is more.
    """
    from rhinomcp.server import get_rhino_connection

    params: dict[str, Any] = {"limit": limit, "offset": offset, "ascending": ascending}
    if section is not None:
        params["section"] = section
    if sort is not None:
        params["sort"] = sort
    if ids:
        params["ids"] = ids
    if joint_type is not None:
        params["joint_type"] = joint_type
    if min_tension_n is not None:
        params["min_tension_n"] = min_tension_n
    if reached_capacity_only:
        params["reached_capacity_only"] = True

    rhino = get_rhino_connection()
    return rhino.send_command("get_stability_report", params)


@mcp.tool()
async def assign_mass(
    density: float | None = None,
    mass: float | None = None,
    ids: list[str] | None = None,
    names: list[str] | None = None,
    layer: str | list[str] | None = None,
    selected: bool = False,
    overwrite: bool = True,
) -> dict[str, Any]:
    """Assign the mass that evaluate_stability needs, without prompting in Rhino.

    Every node the stability evaluator sees must carry a positive mass, stored
    on the object as canonical kilograms. Assign it here rather than through
    the interactive Rhino commands, which stop and ask per object or per layer.

    Args:
        density: Material density, stated in the document's own units - kg/m³ in
            a metric document, lbm/ft³ in an imperial one, the same rule every
            length in every other tool follows. Read `units` from
            get_document_info before choosing the number. Each object's mass
            follows from its own closed volume, so this is the right choice
            whenever the geometry is solid. Objects with no computable volume
            are reported under "skipped" rather than guessed at.
        mass: One mass applied to every object in the scope, stated in the
            document's own units - kg in a metric document, lbm in an imperial
            one. Use it for geometry that is not a closed solid, or to model a
            part as heavier or lighter than its volume implies. Pass exactly
            one of density or mass.
        ids: Object GUIDs to assign.
        names: Object names to assign.
        layer: Layer name, or list of names, to assign.
        selected: When True, assign to the current selection.
        overwrite: When False, objects that already carry a mass keep it and
            are reported under "skipped" with the mass they kept, which makes
            this the way to audit a model's masses without changing them.
            Defaults to True.

    Omitting every scope argument assigns the whole document, matching how the
    connectivity graph and the evaluator read an omitted scope.

    Returns per-object masses in kg, the volumes used, anything skipped and
    why, and the input value with the unit it was read in, so the caller can
    confirm the number was taken the way it was meant. Whatever the document's
    units, mass is stored on the object in canonical kilograms.

    Two totals, because they answer different questions: `assigned_mass_kg` is
    what this call wrote, and `total_mass_kg` is what the scope weighs -
    assigned plus already carried, the same quantity evaluate_stability
    reports under that name. They are equal only when nothing was skipped.
    """
    from rhinomcp.server import get_rhino_connection

    params: dict[str, Any] = {"overwrite": overwrite}
    if density is not None:
        params["density"] = density
    if mass is not None:
        params["mass"] = mass
    if ids:
        params["ids"] = ids
    if names:
        params["names"] = names
    if layer is not None:
        params["layer"] = layer
    if selected:
        params["selected"] = True

    rhino = get_rhino_connection()
    return rhino.send_command("assign_mass", params)


@mcp.tool()
async def assign_joint_type(
    joint_type: str | None = None,
    layer: str | list[str] | None = None,
    with_layer: str | list[str] | None = None,
    with_ids: list[str] | None = None,
    with_names: list[str] | None = None,
    with_ground: bool = False,
    ids: list[str] | None = None,
    names: list[str] | None = None,
    selected: bool = False,
    clear: bool = False,
    prune: bool = False,
    capacity_kn: float | None = None,
) -> dict[str, Any]:
    """State what the connections in a model are, as rules rather than per joint.

    Connection type is domain knowledge and geometry cannot supply it: a
    screwed panel and a dry-stacked one look identical to an intersection
    test. So state it the way an engineer knows it - by construction type,
    for a pair of element classes - and the evaluator resolves each joint.

    Read by elements mode with integrator="rigid_bodies". Ignored by the
    other modes, which have their own fixed idea of what a joint is.

    Args:
        joint_type: "fixed", "pin" or "contact".

            "fixed" - carries force and moment over the measured bearing,
            always. A moment connection: beam to column, a plate welded or
            bolted rigid. Synonyms: welded, weld, moment.
            "pin" - the bearing collapses to its centre, so it carries force
            in three directions and no moment. Truss to truss, a single
            bolt. Synonyms: pinned, hinge.
            "contact" - the bearing pushes and does not pull, with friction
            across it, so it opens as load leaves it. Dry masonry, a beam on
            a corbel, a precast panel on a pad. Synonyms: bearing, dry.

        layer: One side of the rule, named by layer. A name or a list.
        ids: One side, named by object GUID. Use with with_layer/with_ids for
            a pair rule; alone, it is an element rule.
        names: One side, named by object name. Resolved to GUIDs as it is
            written, since a name can be changed or duplicated later.
        with_layer: The other side, by layer, making this a rule about the
            joints *between* two classes - "beam to column is fixed".
        with_ids: The other side, by object GUID.
        with_ground: Makes the ground the other side, so the rule says how this
            element is founded rather than how it meets another element. A base
            is `contact` unless a rule says otherwise: it bears on the floor,
            can lift off it and can slide on it. `fixed` or `pin` founds it.
            A pad cast into a footing and one set down on gravel are drawn
            identically, so which it is has to be stated.

                assign_joint_type(joint_type="fixed", layer="PAD",
                                  with_ground=True)
        with_names: The other side, by object name.
        selected: When True, applies to the current selection.
        clear: When True, removes the rule instead of writing it.
        prune: When True, removes every rule that can no longer match - one
            naming an object that has been deleted, or a layer that no longer
            exists - and returns what it removed. Pass it on its own; no
            joint_type is needed. Rules naming objects outlive those objects,
            since they live in the document and the objects do not, so they
            accumulate quietly. Every other call reports them under "rules"
            with a "stale" field, so they are visible before this is wanted.
            Not done automatically: a deleted object can be undone, and a rule
            dropped in between would not come back with it.

    Any "with_" argument makes it a pair rule and requires one of layer/ids/
    names for the other side. Order never matters: Beams/Columns and
    Columns/Beams write and match the same rule.

    Precedence, most specific first: two named objects, then one object and
    one layer, then two layers, then what one element says about its own
    joints, then evaluate_stability's joint_type as the default. So "this
    beam meets that column as a pin" survives a blanket "beams meet columns
    welded" rather than being averaged with it.

    Where two elements state different types and no pair rule covers them,
    the weaker governs - a hinge assumed where a moment connection exists
    reads softer and more mechanism-prone than the truth, which fails safe
    for a stability verdict, and unlike "last rule wins" it does not depend
    on the order the rules were given in.

    Element rules are stored on the object beside its mass, so they travel
    with a copy. Pair rules are stored in the document, because there is
    nowhere on a beam to record what it does when it meets a column.

    Called with no arguments at all it lists the rules and changes nothing,
    counting how many are stale under "stale_rules".

    The evaluation reports what each joint resolved to and which rule
    decided it, under "nodes".

    Examples:
        assign_joint_type(joint_type="welded", layer="Beams",
                          with_layer="Columns")
        assign_joint_type(joint_type="pin", layer="Truss")
        assign_joint_type(joint_type="contact", ids=[...])
        assign_joint_type(joint_type="pin", ids=[beam],
                          with_ids=[column])          # this joint only

    capacity_kn: How much tension the joint can hold, in kilonewtons. Absent means
        unlimited, which is what every joint is until someone says otherwise - so a
        model without capacities behaves exactly as it did before.

        Tension only. Compression is limited by the material of the things meeting,
        not by whatever holds them together, and a "contact" joint refuses tension
        outright - so this binds on the joints you declared strong, which is where
        the model is otherwise unboundedly optimistic.

        The limit is shared among the joint's bearing points, so it gives a moment
        capacity as well as a force one: load an eccentric bearing hard enough and
        its far point reaches the limit first and stops holding, so the joint sheds
        its edge and rotates rather than failing everywhere at once.

        A joint at its limit yields rather than breaking - the force holds there and
        the structure redistributes, and if it cannot it moves, which the verdict is
        already watching for. evaluate_stability reports joints_with_capacity and
        joints_at_capacity, so a verdict that changed because a joint yielded says so.

        assign_joint_type(joint_type="pin", capacity_kn=12, layer="Truss")
    """
    from rhinomcp.server import get_rhino_connection

    params: dict[str, Any] = {}
    if joint_type is not None:
        params["joint_type"] = joint_type
    if capacity_kn is not None:
        params["capacity_kn"] = capacity_kn
    if layer is not None:
        params["layer"] = layer
    if with_layer is not None:
        params["with_layer"] = with_layer
    if with_ground:
        params["with_ground"] = True
    if with_ids:
        params["with_ids"] = with_ids
    if with_names:
        params["with_names"] = with_names
    if ids:
        params["ids"] = ids
    if names:
        params["names"] = names
    if selected:
        params["selected"] = True
    if clear:
        params["clear"] = True
    if prune:
        params["prune"] = True

    rhino = get_rhino_connection()
    return rhino.send_command("assign_joint_type", params)


@mcp.tool()
async def graph_display(
    enabled: bool | None = None,
    scope: str | None = None,
    ids: list[str] | None = None,
    layer: str | list[str] | None = None,
    selected: bool = False,
) -> dict[str, Any]:
    """Show or hide the connectivity-graph overlay in Rhino's viewport.

    The overlay draws what the evaluator will actually see: which elements
    touch, where they touch, the bearing surface each joint is built over,
    and what joint type each one resolves to. It is the check that the model
    being solved is the model that was meant - a joint the graph never
    found cannot be assigned a type, and a bearing measured on the wrong
    plane restrains the wrong rotation.

    Colours follow joint type: welded amber, pin blue, contact green. A
    joint drawn dim took evaluate_stability's default because no rule named
    it; a bright one was named by a rule. Both solve the same way - what
    differs is whether anyone said so.

    Called with no arguments this reports the current state and changes
    nothing.

    Args:
        enabled: Show or hide the overlay.
        scope: Pass "all" to graph the whole document. Large documents
            truncate, and the overlay says so when they do.
        ids: Graph only these objects.
        layer: Graph only this layer, or these layers.
        selected: Graph only the current selection.

    Omitting every scope argument leaves whatever scope is pinned alone,
    which is different from asking for the whole document.

    Returns the resulting state: enabled and the scope.
    """
    from rhinomcp.server import get_rhino_connection

    params: dict[str, Any] = {}
    if enabled is not None:
        params["enabled"] = enabled
    if scope is not None:
        params["scope"] = scope
    if ids:
        params["ids"] = ids
    if layer is not None:
        params["layer"] = layer
    if selected:
        params["selected"] = True

    rhino = get_rhino_connection()
    return rhino.send_command("graph_display", params)


@mcp.tool()
async def get_selected_objects() -> str:
    """Get id, name, type, and layer of all currently selected objects in Rhino."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_selected_objects", {})
    count = result.get("count", 0)
    return f"Selected {count} object(s):\n" + "\n".join(
        f"  - {o['name']} ({o['type']}) on layer '{o['layer']}'" 
        for o in result.get("selected", [])
    )

@mcp.tool()
async def select_objects(ids: list[str] | None = None, names: list[str] | None = None,
                         layer: str | None = None, type: str | None = None) -> str:
    """Select objects by ID, name, layer, or type. Filters are OR logic — any matching filter includes the object.

    Args:
        ids: List of object GUIDs.
        names: List of object names (partial match not supported).
        layer: Layer name — selects all objects on that layer.
        type: Object type string. Valid values: Brep, Mesh, Curve, Extrusion, Point, PointSet, Annotation, Hatch, Light, SubD.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {}
    if ids: params["ids"] = ids
    if names: params["names"] = names
    if layer: params["layer"] = layer
    if type: params["type"] = type
    result = rhino.send_command("select_objects_by_filter", params)
    return result.get("message", "Selection complete.")

@mcp.tool()
async def deselect_all() -> str:
    """Deselect all objects in the Rhino document."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    rhino.send_command("deselect_all", {})
    return "All objects deselected."

@mcp.tool()
async def zoom_to_objects(ids: list[str] | None = None) -> str:
    """Zoom viewport to selected objects (or currently selected if no IDs provided)."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {"ids": ids} if ids else {}
    result = rhino.send_command("zoom_to_objects", params)
    return result.get("message") or result.get("error", "Zoom complete.")

@mcp.tool()
async def capture_view(
    view: str = "perspective",
    ids: list[str] | None = None,
    selected: bool = False,
    all_visible: bool = False,
    fit: bool = True,
    padding: float = 1.15,
    display_mode: str = "Shaded",
    resolution: str = "medium",
    width: int | None = None,
    height: int | None = None,
    camera_location: list[float] | None = None,
    camera_target: list[float] | None = None,
    camera_up: list[float] | None = None,
    lens_mm: float | None = None,
    draw_grid: bool = False,
    draw_axes: bool = False,
    background: str = "viewport",
    preserve_view: bool = True,
) -> list:
    """Temporarily frame targets in Rhino, capture PNG, and return image plus compact metadata.

    Args:
        view: perspective, isometric, top, front, or right.
        ids: Explicit object GUIDs to frame.
        selected: Frame currently selected objects.
        all_visible: Frame all visible objects.
        fit: Zoom/frustum-fit target bounds before capture.
        padding: Target bounds padding multiplier.
        display_mode: Rhino display mode name, for example Shaded, Rendered, Wireframe, Technical.
        resolution: low (640x480), medium (960x720), high (1280x900) or print (2560x1800).
            Screen-space items - overlay text, point markers, line widths - scale with
            the size, so a large capture is not a small one with tiny labels.
        width: Optional explicit width override, clamped to 256..3840 by the plugin.
        height: Optional explicit height override, clamped to 256..3840 by the plugin.
        camera_location: Optional explicit camera location [x, y, z].
        camera_target: Optional explicit camera target [x, y, z].
        camera_up: Optional camera up vector [x, y, z].
        lens_mm: Optional perspective lens length.
        draw_grid: Include grid in capture.
        draw_axes: Include axes in capture.
        background: "viewport" keeps the display mode's own background; "white" drops it
            and flattens onto white; "transparent" keeps the alpha. Display conduits -
            the graph overlay, the settled pose - are drawn in every case.
        preserve_view: Restore the active camera, projection, lens, frustum, and display mode after capture. Defaults True.
    """
    from rhinomcp.server import get_rhino_connection

    rhino = get_rhino_connection()
    params = {
        "view": view,
        "selected": selected,
        "all_visible": all_visible,
        "fit": fit,
        "padding": padding,
        "display_mode": display_mode,
        "resolution": resolution,
        "draw_grid": draw_grid,
        "draw_axes": draw_axes,
        "background": background,
        "preserve_view": preserve_view,
    }
    if ids: params["ids"] = ids
    if width is not None: params["width"] = width
    if height is not None: params["height"] = height
    if camera_location is not None: params["camera_location"] = camera_location
    if camera_target is not None: params["camera_target"] = camera_target
    if camera_up is not None: params["camera_up"] = camera_up
    if lens_mm is not None: params["lens_mm"] = lens_mm

    result = rhino.send_command("capture_view", params)
    if "error" in result:
        return [result["error"]]

    png_base64 = result.get("png_base64")
    if not png_base64:
        return ["Capture failed: missing PNG data"]

    metadata = result.get("metadata", {})
    image = Image(data=base64.b64decode(png_base64), format="png")
    return [image, json.dumps(metadata, separators=(",", ":"))]

@mcp.tool()
async def get_viewport_info() -> str:
    """Get camera, projection, lens, display mode, and active state for all Rhino viewports."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_viewport_info", {})
    vps = result.get("viewports", [])
    return f"Viewports ({result.get('count', 0)}):\n" + "\n".join(
        (
            f"  - {'* ' if v.get('active') else ''}{v['name']} "
            f"[{v.get('projection', 'unknown')}, "
            f"lens={v.get('lensMm') if v.get('lensMm') is not None else 'n/a'} mm, "
            f"mode={v.get('displayMode', 'unknown')}] at {v['cameraLocation']}"
        )
        for v in vps
    )

@mcp.tool()
async def rename_layer(id: str, new_name: str) -> str:
    """Rename a layer by ID.
    
    Args:
        id: Layer GUID.
        new_name: New layer name.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("rename_layer", {"id": id, "new_name": new_name})
    return result.get("message", result.get("error", "Layer renamed."))

@mcp.tool()
async def move_objects_to_layer(ids: list[str], layer: str) -> str:
    """Move objects to a specific layer by name.
    
    Args:
        ids: List of object GUIDs to move.
        layer: Target layer name.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("move_objects_to_layer", {"ids": ids, "layer": layer})
    return result.get("message", result.get("error", "Move complete."))

@mcp.tool()
async def get_layer_states() -> str:
    """Get the current state (visible/locked/color) of all layers."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_layer_states", {})
    layers = result.get("layers", [])
    return f"Layers ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {l['name']} {'🔒' if l['locked'] else ''} {'👁️' if l['visible'] else '🚫'} [{l['color']}]" 
        for l in layers
    )

@mcp.tool()
async def save_layer_state(name: str) -> str:
    """Save the current layer visibility and lock state under a name. State is in-memory only — lost if the Rhino plugin restarts."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("save_layer_state", {"name": name})
    return result.get("message", result.get("error", "Layer state saved."))

@mcp.tool()
async def restore_layer_state(name: str) -> str:
    """Restore a previously saved layer visibility and lock state. Only restores states saved in the current session."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("restore_layer_state", {"name": name})
    return result.get("message", result.get("error", "Layer state restored."))

@mcp.tool()
async def get_named_views() -> str:
    """List the document's named views with their projection and camera framing.

    Unlike layer states, named views are stored in the Rhino document itself,
    so they survive a plugin or Rhino restart and are saved with the file.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_named_views", {})
    if result.get("error"):
        return f"Error: {result['error']}"
    views = result.get("named_views", [])
    if not views:
        return "No named views in this document."
    lines = []
    for v in views:
        lens = f" {v['lensMm']}mm" if v.get("lensMm") else ""
        lines.append(
            f"  - {v['name']} [{v['projection']}{lens}] "
            f"cam {v.get('cameraLocation')} -> {v.get('cameraTarget')}"
        )
    return f"Named views ({result.get('count', 0)}):\n" + "\n".join(lines)

@mcp.tool()
async def save_named_view(name: str, viewport: str | None = None) -> str:
    """Save a viewport's current camera as a named view in the document.

    Args:
        name: Name to save under. An existing named view of the same name is
            replaced, so that the name stays unambiguous to restore by.
        viewport: Viewport to capture, by name. Defaults to the active one.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params: dict[str, Any] = {"name": name}
    if viewport is not None:
        params["viewport"] = viewport
    result = rhino.send_command("save_named_view", params)
    return result.get("message", result.get("error", "Named view saved."))

@mcp.tool()
async def restore_named_view(name: str, viewport: str | None = None) -> str:
    """Restore a named view's camera into a viewport.

    Args:
        name: Name of the named view to restore.
        viewport: Viewport to restore into, by name. Defaults to the active one.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params: dict[str, Any] = {"name": name}
    if viewport is not None:
        params["viewport"] = viewport
    result = rhino.send_command("restore_named_view", params)
    return result.get("message", result.get("error", "Named view restored."))

@mcp.tool()
async def delete_named_view(name: str) -> str:
    """Delete a named view from the document.

    Args:
        name: Name of the named view to remove.
    """
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("delete_named_view", {"name": name})
    return result.get("message", result.get("error", "Named view deleted."))

@mcp.tool()
async def get_materials() -> str:
    """Get all materials in the Rhino document."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_materials", {})
    mats = result.get("materials", [])
    return f"Materials ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {m['name']} [{m['diffuseColor']}]" for m in mats
    )

@mcp.tool()
async def create_material(name: str = "NewMaterial", r: int = 128, g: int = 128, b: int = 128) -> str:
    """Create a new Rhino material with a diffuse color. Only diffuse color is supported. Returns the material index needed for set_object_material."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("create_material", {"name": name, "r": r, "g": g, "b": b})
    return result.get("message", result.get("error", "Material created."))

@mcp.tool()
async def set_object_material(ids: list[str], material_name: str | None = None, material_index: int | None = None) -> str:
    """Assign a material to objects. Prefer material_index (faster, unambiguous). material_name used only if index not provided. Use get_materials to find available materials and their indices."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {"ids": ids}
    if material_name: params["material_name"] = material_name
    if material_index is not None: params["material_index"] = material_index
    result = rhino.send_command("set_object_material", params)
    return result.get("message", result.get("error", "Material assigned."))

@mcp.tool()
async def get_object_materials(ids: list[str] | None = None) -> str:
    """Get materials assigned to objects. If no IDs, returns all objects."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    params = {}
    if ids: params["ids"] = ids
    result = rhino.send_command("get_object_materials", params)
    objs = result.get("objects", [])
    return f"Object materials ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {o['name']} -> {o['material_name']}" for o in objs
    )
