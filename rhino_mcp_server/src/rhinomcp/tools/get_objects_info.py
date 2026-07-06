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
    - geometry_detail: one of "bbox" | "obb_pose" | "ortho3" (default "obb_pose").
      - "bbox": world AABB only.
      - "obb_pose": pose-aware detailed geometry (OBB extents + pose, one silhouette).
      - "ortho3": up to three orthographic outline views (top/front/right) of a solid/mesh.
        Use it to disambiguate shapes that share the same OBB extents and single silhouette
        (cone vs cylinder vs tapered box). Non-solid/mesh objects fall back to "obb_pose".
    - include_world: Include world-space duplicates such as world points and world corners.

    Return value (per object) for geometry_detail="ortho3", under object["geometry"]:
      - obb.extents: [x_len, y_len, z_len] full side lengths in the pose local frame.
      - pose.world_from_local: {R (3x3), t} local->world transform.
      - views_frame: string stating the axis mapping and that views are silhouettes, e.g.
        "local pose; top=[X,Y] front=[X,Z] right=[Y,Z]; shared origin; silhouette (direction-agnostic)".
      - views: list of {axis, points} where axis is "top"|"front"|"right" and points is a
        closed outer outline as 2D [u,v] pairs in that view's plane (units match obb.extents).
        Add include_world=true to also get points_world ([x,y,z]) per view.
      - views_dropped: optional map {dropped_axis: kept_axis}, e.g. {"right":"front"} meaning
        the right silhouette is ~identical to front (object symmetric there) - NOT missing data.
      Views share the pose origin and are aligned like an engineering drawing, so a given axis
      (e.g. X) has the same extent across the views that use it.
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
