from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any


@mcp.tool()
def rebase_object_pose(
    ctx: Context,
    id: str = None,
    name: str = None,
    z_direction: str = None,
    x_direction: str = None
) -> Dict[str, Any]:
    """
    Rebase canonical pose without moving geometry.

    Keeps orientation basis from current pose and only relabels/signed-swaps axes.
    Translation anchor is always the current geometry bbox center.
    Optionally, directional hints can be requested by setting:
    - z_direction: +z or -z
    - x_direction: +x, -x, +y, or -y
    Hints are interpreted in world axes and resolved to the closest signed
    axis permutation of the existing pose frame.

    Parameters:
    - id: The id of the object
    - name: The name of the object
    - z_direction: Optional signed axis (+z/-z)
    - x_direction: Optional signed axis (+x/-x/+y/-y)
    """
    try:
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
        if z_direction is not None:
            params["z_direction"] = z_direction
        if x_direction is not None:
            params["x_direction"] = x_direction

        return rhino.send_command("rebase_object_pose", params)
    except Exception as e:
        logger.error(f"Error rebasing object pose: {str(e)}")
        return {"error": str(e)}
