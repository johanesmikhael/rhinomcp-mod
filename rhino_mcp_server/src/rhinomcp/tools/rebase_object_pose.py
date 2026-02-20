from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any


@mcp.tool()
def rebase_object_pose(
    ctx: Context,
    id: str = None,
    name: str = None,
    translation_mode: str = "pose_t"
) -> Dict[str, Any]:
    """
    Rebase canonical pose without moving geometry.

    Sets stored pose to identity rotation and keeps translation anchor from:
    - "pose_t": current pose translation t (default)
    - "bbox_center": current geometry bbox center

    Parameters:
    - id: The id of the object
    - name: The name of the object
    - translation_mode: "pose_t" or "bbox_center"
    """
    try:
        rhino = get_rhino_connection()

        params: Dict[str, Any] = {}
        if id is not None:
            params["id"] = id
        if name is not None:
            params["name"] = name
        if translation_mode is not None:
            params["translation_mode"] = translation_mode

        return rhino.send_command("rebase_object_pose", params)
    except Exception as e:
        logger.error(f"Error rebasing object pose: {str(e)}")
        return {"error": str(e)}
