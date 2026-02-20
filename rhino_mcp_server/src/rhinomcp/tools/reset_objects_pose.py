from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any, List


@mcp.tool()
def reset_objects_pose(
    ctx: Context,
    objects: List[Dict[str, Any]] = None,
    all: bool = None
) -> Dict[str, Any]:
    """
    Reset pose for multiple objects.

    Each object entry can include:
    - id or name
    - reset_rotation: Optional bool (default True)
    - reset_translation: Optional bool (default True)
    - target_translation: Optional [x, y, z]

    Parameters:
    - objects: List[ObjectResetPoseSpec]
      ObjectResetPoseSpec schema:
      - id or name: required selector
      - reset_rotation: optional bool (default True)
      - reset_translation: optional bool (default True)
      - target_translation: optional [x, y, z]
    - all: Optional bool. If true, apply to all objects.
    """
    try:
        if (not objects) and not all:
            return {"error": "objects must be a non-empty list unless all=true"}

        for index, entry in enumerate(objects or []):
            if not isinstance(entry, dict):
                return {"error": f"objects[{index}] must be a dictionary"}
            if "id" not in entry and "name" not in entry:
                return {"error": f"objects[{index}] requires 'id' or 'name'"}

        rhino = get_rhino_connection()

        params: Dict[str, Any] = {}
        if objects is not None:
            params["objects"] = objects
        if all is not None:
            params["all"] = all

        return rhino.send_command("reset_objects_pose", params)
    except Exception as e:
        logger.error(f"Error resetting objects pose: {str(e)}")
        return {"error": str(e)}
