from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import List


@mcp.tool()
def delete_objects(
    ctx: Context,
    ids: List[str] = None,
    names: List[str] = None,
    confirm: bool = None
) -> str:
    """
    Delete multiple objects from the Rhino document.

    Parameters:
    - ids: Optional list of object ids to delete
    - names: Optional list of object names to delete (names must be unique)
    - confirm: Required boolean, must be True to proceed
    """
    try:
        rhino = get_rhino_connection()

        if not confirm:
            return "Error deleting objects: confirm=true is required"

        command_params = {"confirm": True}
        if ids:
            command_params["ids"] = ids
        if names:
            command_params["names"] = names

        result = rhino.send_command("delete_objects", command_params)
        return f"Deleted {result['count']} objects"
    except Exception as e:
        logger.error(f"Error deleting objects: {str(e)}")
        return f"Error deleting objects: {str(e)}"
