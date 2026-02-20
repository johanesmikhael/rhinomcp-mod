from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, Dict, List


@mcp.tool()
def copy_objects(
    ctx: Context,
    objects: List[Dict[str, Any]]
) -> str:
    """
    Copy multiple objects in the Rhino document.

    Parameters:
    - objects: List[ObjectCopySpec]
      ObjectCopySpec schema:
      - id or name: required selector
      - translation: Optional [x, y, z] translation vector for the copy
    """
    try:
        if not objects:
            return "Error copying objects: objects must be a non-empty list"

        for index, entry in enumerate(objects):
            if not isinstance(entry, dict):
                return f"Error copying objects: objects[{index}] must be a dictionary"
            if "id" not in entry and "name" not in entry:
                return f"Error copying objects: objects[{index}] requires 'id' or 'name'"

        rhino = get_rhino_connection()
        command_params: Dict[str, Any] = {"objects": objects}
        result = rhino.send_command("copy_objects", command_params)
        return f"Copied {result['copied']} objects"
    except Exception as e:
        logger.error(f"Error copying objects: {str(e)}")
        return f"Error copying objects: {str(e)}"
