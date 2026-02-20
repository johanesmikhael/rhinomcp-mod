from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any, List


@mcp.tool()
def reset_object_pose(
    ctx: Context,
    id: str = None,
    name: str = None,
    reset_rotation: bool = True,
    reset_translation: bool = True,
    target_translation: List[float] = None
) -> Dict[str, Any]:
    """
    Reset an object's pose relative to world coordinates.

    - reset_rotation=True aligns object orientation with world axes.
    - reset_translation=True moves object pose origin to target_translation
      (default [0, 0, 0]).

    Parameters:
    - id: The id of the object to reset
    - name: The name of the object to reset
    - reset_rotation: Whether to reset rotation (default True)
    - reset_translation: Whether to reset translation (default True)
    - target_translation: Optional world target [x, y, z] (default [0, 0, 0])
    """
    try:
        rhino = get_rhino_connection()

        params: Dict[str, Any] = {}
        if id is not None:
            params["id"] = id
        if name is not None:
            params["name"] = name

        params["reset_rotation"] = reset_rotation
        params["reset_translation"] = reset_translation
        if target_translation is not None:
            params["target_translation"] = target_translation

        return rhino.send_command("reset_object_pose", params)
    except Exception as e:
        logger.error(f"Error resetting object pose: {str(e)}")
        return {"error": str(e)}
