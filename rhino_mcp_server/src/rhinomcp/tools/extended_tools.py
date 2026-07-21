"""Extended Rhino MCP tools for selection, layers, materials, and view capture."""
import base64
import json

from mcp.server.fastmcp import Image
from rhinomcp.server import mcp

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
) -> list:
    """Set Rhino active viewport, frame targets, capture PNG, and return image plus compact metadata.

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
    """Get information about all viewports in the Rhino document."""
    from rhinomcp.server import get_rhino_connection
    rhino = get_rhino_connection()
    result = rhino.send_command("get_viewport_info", {})
    vps = result.get("viewports", [])
    return f"Viewports ({result.get('count', 0)}):\n" + "\n".join(
        f"  - {v['name']} at {v['cameraLocation']}" for v in vps
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
