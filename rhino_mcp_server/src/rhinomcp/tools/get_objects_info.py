from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any, List


@mcp.tool()
def get_objects_info(
    ctx: Context,
    objects: List[Dict[str, Any]],
    include_attributes: bool = False,
    outline_max_points: int = 0,
    geometry_detail: str = "obb_pose",
    include_world: bool = False,
) -> Dict[str, Any]:
    """
    Get detailed information for multiple objects by explicit selectors.

    Parameters:
    - objects: List[ObjectSelector]
      ObjectSelector schema:
      - id or name: required selector
    - include_attributes: Optional bool to include user attributes
    - outline_max_points: Optional int for geometry outline simplification
    - geometry_detail: "bbox" for world AABB only, "obb_pose" for pose-aware detailed geometry,
      "ortho3" for up to three orthographic outline views (top/front/right) of a solid/mesh.
      Use "ortho3" to disambiguate shapes with equal OBB extents (cone vs cylinder vs tapered box);
      redundant and edge-on views are dropped, and views_dropped maps each dropped view to its kept twin.
      For non-solid/mesh objects "ortho3" falls back to "obb_pose".
    - include_world: Include world-space duplicates such as world points and world corners.
    """
    try:
        if not objects:
            return {"error": "objects must be a non-empty list"}

        for index, entry in enumerate(objects):
            if not isinstance(entry, dict):
                return {"error": f"objects[{index}] must be a dictionary"}
            if "id" not in entry and "name" not in entry:
                return {"error": f"objects[{index}] requires 'id' or 'name'"}

        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "objects": objects,
            "include_attributes": include_attributes,
            "geometry_detail": geometry_detail,
            "include_world": include_world,
        }
        if outline_max_points is not None:
            params["outline_max_points"] = outline_max_points

        return rhino.send_command("get_objects_info", params)
    except Exception as e:
        logger.error(f"Error getting objects info: {str(e)}")
        return {"error": str(e)}
