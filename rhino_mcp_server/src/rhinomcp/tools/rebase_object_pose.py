from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any


@mcp.tool()
def rebase_object_pose(
    ctx: Context,
    id: str = None,
    name: str = None,
    translation_mode: str = "pose_t",
    z_direction: str = None,
    x_direction: str = None
) -> Dict[str, Any]:
    """
    Rebase canonical pose without moving geometry.

    Sets stored pose to identity rotation and keeps translation anchor from:
    - "pose_t": current pose translation t (default)
    - "bbox_center": current geometry bbox center
    Optionally, a directional workplane can be requested by setting:
    - z_direction: +z or -z
    - x_direction: +x, -x, +y, or -y

    Parameters:
    - id: The id of the object
    - name: The name of the object
    - translation_mode: "pose_t" or "bbox_center"
    - z_direction: Optional signed axis (+z/-z)
    - x_direction: Optional signed axis (+x/-x/+y/-y)
    """
    try:
        if translation_mode is not None and translation_mode not in {"pose_t", "bbox_center"}:
            return {"error": "translation_mode must be 'pose_t' or 'bbox_center'"}
        if z_direction is not None and z_direction not in {"+z", "-z"}:
            return {"error": "z_direction must be '+z' or '-z'"}
        if x_direction is not None and x_direction not in {"+x", "-x", "+y", "-y"}:
            return {"error": "x_direction must be '+x', '-x', '+y' or '-y'"}

        rhino = get_rhino_connection()

        params: Dict[str, Any] = {}
        if id is not None:
            params["id"] = id
        if name is not None:
            params["name"] = name
        if translation_mode is not None:
            params["translation_mode"] = translation_mode
        if z_direction is not None:
            params["z_direction"] = z_direction
        if x_direction is not None:
            params["x_direction"] = x_direction

        return rhino.send_command("rebase_object_pose", params)
    except Exception as e:
        logger.error(f"Error rebasing object pose: {str(e)}")
        return {"error": str(e)}
