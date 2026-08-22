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
    torque_gain: float | None = None,
    duration_seconds: float | None = None,
    damping_ratio: float | None = None,
    integrator: str | None = None,
    lateral_load_fraction: float | None = None,
    joint_stiffness_n_per_m: float | None = None,
    stability_threshold: float | None = None,
    rigid_strength: float | None = None,
    ground_support_stiffness_n_per_m: float | None = None,
    floor_z: float | None = None,
    gravity: float = 9.80665,
    solver_substeps: int = 1,
    display: bool = False,
    graph: str | dict[str, Any] | None = None,
    layer: str | list[str] | None = None,
    ids: list[str] | None = None,
    bbox: list[list[float]] | None = None,
    bbox_mode: str = "intersects",
    selected: bool = False,
) -> dict[str, Any]:
    """Experimentally evaluate the active model as one welded rigid assembly.

    The model must have a connectivity graph and positive mass assigned to
    every graph node. Graph edges are not physical joints in this mode: all
    nodes receive one shared rigid transform. By default the graph is read
    from the active document.

    Args:
        mode: "welded" (default) treats the whole scope as one rigid body and
            answers whether it tips over. "pinned" gives every element its own
            rigid body, joined at the connectivity graph's contact points, and
            answers whether the assembly is a mechanism - whether any element
            can rotate or slide off its supports. Neither subsumes the other: a
            pin holds in tension, so pinned mode cannot see an element toppling
            off another, and welded mode cannot see a mechanism at all. Run
            both. "contact" also gives every element its own body, but joins
            them across bearing surfaces that carry compression and no tension,
            so an element can rotate off its support or lift away - the closest
            of the three to a dry-stacked assembly, and the only one that can
            fail a single element rather than the whole scope.
            Pinned and contact modes report per-element displacement and rotation and
            names the element that moved furthest, and its result carries no
            floor strength, assembly transform or support margin.
        joint_penetration: Contact mode only. How far a bearing surface may close
            under its own load, in document units. This is the automatic mode's
            real knob; it sets the per-step motion directly.
        ground_settlement: Contact mode only. How far a body may settle into the
            ground under its own load, in document units. Separate from the
            joints because the ground is a soil and the joints are not.
        joint_stiffness_n_per_m: Pinned modes only. Axial joint stiffness in N/m,
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
        lateral_load_fraction: Pinned modes only. The sideways probe that
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
        rigid_strength: Welded mode only: how rigid the single assembly body is.
            It no longer reaches the pinned joints - use joint_stiffness_n_per_m
            for those. When omitted, Rhino sizes it above the ground stiffness. The two goals are blended by
            weight, so a rigid strength below the floor lets the floor deform
            the assembly it is supporting and a sound structure reads as
            unstable. Pass a value only to study a deliberately compliant body.
        ground_support_stiffness_n_per_m: How stiffly the ground holds a standing
            vertex, in N/m of its tributary area. When omitted, Rhino sizes it
            from the assembly's total mass so that settling stays within a tenth
            of the stability threshold; a sound structure would otherwise read as
            unstable purely from sinking. Not a subgrade modulus - it is
            multiplied by tributary areas that include each corner's share of the
            side faces meeting there, so the product is the quantity with
            meaning. Accepted as floor_strength under its old name.
        floor_z: World Z elevation of the collision floor, in document units.
            When omitted, Rhino places the floor at the underside of the scoped
            assembly. Pass a value only to hold the floor at a fixed level: a
            scope that leaves out the pads its columns stand on would otherwise
            start in mid-air and spend the run falling to world zero.
        gravity: Downward gravitational acceleration in m/s².
        duration_seconds: pinned_dynamic mode only. How long to simulate, in seconds.
            Unlike a step count this means the same thing on every model: a mechanism
            with a tenth of gravity available to it covers 50 mm in 0.32 s, so the
            default 0.5 s separates falling from standing with room to spare.
        damping_ratio: pinned_dynamic mode only. Viscous damping as a fraction of
            critical. Defaults to 0.02, the low end of what codes assume for steel
            framing. With none at all a sound structure oscillates forever about its
            static deflection and peaks at twice it. The two integrators do not mean
            the same thing by it: "particles" damps each particle against its own local
            stiffness, which over-damps the slow global mode, while "rigid_bodies"
            damps each joint against relative motion there, which barely touches a mode
            where both ends of a joint move together. The rigid path typically needs
            0.2 to settle inside a half-second run.
        integrator: pinned_dynamic mode only. "particles" (default) or "rigid_bodies".
            They answer differently and the difference is not small.

            "particles" holds each body's points to a fitted frame. It reproduces a
            closed-form axial deflection to about 1%, is the calibrated default, and
            cannot represent free motion at all - a body with nothing under it falls at
            0.2% of g, so an unstable verdict can only be reached by deformation
            crossing a limit, never by something toppling or dropping. Every joint is a
            point, so a wall and a column of the same axial stiffness give the same
            answer.

            "rigid_bodies" integrates the body itself under F = ma and Euler's
            equations. It falls correctly, to one part in ten thousand, and builds each
            joint over the bearing region the graph measured - so a 1150 mm wall comes
            out 14 times stiffer in its own plane than across it, where the particle
            path reports no difference. It is opt-in because it is less settled: a
            two-storey stack needs a timestep four times finer than its own rule gives,
            and it wants damping_ratio near 0.2 rather than 0.02.

            Use "rigid_bodies" when walls, slabs or wide bearings carry load, or when
            something has to be able to fall. Use the default otherwise.
        solver_substeps: Kangaroo substeps per solver step. Ignored by pinned_dynamic,
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
    if lateral_load_fraction is not None:
        params["lateral_load_fraction"] = lateral_load_fraction
    if joint_stiffness_n_per_m is not None:
        params["joint_stiffness_n_per_m"] = joint_stiffness_n_per_m
    if torque_gain is not None:
        params["torque_gain"] = torque_gain
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
        density: Material density in kg/m^3, for example 2400 for concrete.
            Each object's mass follows from its own closed volume, so this is
            the right choice whenever the geometry is solid. Objects with no
            computable volume are reported under "skipped" rather than guessed
            at. Document units are converted for you.
        mass: One mass in kilograms applied to every object in the scope. Use
            it for geometry that is not a closed solid, or to model a part as
            heavier or lighter than its volume implies. Pass exactly one of
            density or mass.
        ids: Object GUIDs to assign.
        names: Object names to assign.
        layer: Layer name, or list of names, to assign.
        selected: When True, assign to the current selection.
        overwrite: When False, objects that already carry a mass keep it and
            are reported under "skipped". Defaults to True.

    Omitting every scope argument assigns the whole document, matching how the
    connectivity graph and the evaluator read an omitted scope.

    Returns per-object masses in kg, the volumes used, anything skipped and
    why, and the scope total.
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
        resolution: low (640x480), medium (960x720), or high (1280x900).
        width: Optional explicit width override, clamped by plugin.
        height: Optional explicit height override, clamped by plugin.
        camera_location: Optional explicit camera location [x, y, z].
        camera_target: Optional explicit camera target [x, y, z].
        camera_up: Optional camera up vector [x, y, z].
        lens_mm: Optional perspective lens length.
        draw_grid: Include grid in capture.
        draw_axes: Include axes in capture.
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
