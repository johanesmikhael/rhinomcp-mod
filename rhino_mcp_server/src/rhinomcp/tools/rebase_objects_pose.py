from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Dict, Any, List


@mcp.tool()
def rebase_objects_pose(
    ctx: Context,
    objects: List[Dict[str, Any]] = None,
    all: bool = None
) -> Dict[str, Any]:
    """
    Rebase canonical pose for multiple objects without moving geometry.

    Each object entry can include:
    - id or name
    - translation_mode: Optional "pose_t" or "bbox_center"
    - z_direction: Optional +z/-z
    - x_direction: Optional +x/-x/+y/-y

    Parameters:
    - objects: List[ObjectRebasePoseSpec]
      ObjectRebasePoseSpec schema:
      - id or name: required selector
      - translation_mode: optional "pose_t" or "bbox_center"
      - z_direction: optional +z/-z
      - x_direction: optional +x/-x/+y/-y
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
            mode = entry.get("translation_mode")
            if mode is not None and mode not in {"pose_t", "bbox_center"}:
                return {"error": f"objects[{index}].translation_mode must be 'pose_t' or 'bbox_center'"}
            z_direction = entry.get("z_direction")
            if z_direction is not None and z_direction not in {"+z", "-z"}:
                return {"error": f"objects[{index}].z_direction must be '+z' or '-z'"}
            x_direction = entry.get("x_direction")
            if x_direction is not None and x_direction not in {"+x", "-x", "+y", "-y"}:
                return {"error": f"objects[{index}].x_direction must be '+x', '-x', '+y' or '-y'"}

        rhino = get_rhino_connection()

        params: Dict[str, Any] = {}
        if objects is not None:
            params["objects"] = objects
        if all is not None:
            params["all"] = all

        return rhino.send_command("rebase_objects_pose", params)
    except Exception as e:
        logger.error(f"Error rebasing objects pose: {str(e)}")
        return {"error": str(e)}
