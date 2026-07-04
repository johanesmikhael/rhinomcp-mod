from mcp.server.fastmcp import Context
from rhinomcp.server import get_rhino_connection, mcp, logger
from typing import Any, Dict


@mcp.tool()
def close_file(
    ctx: Context,
    save_changes: bool = False,
    save_path: str = None,
) -> Dict[str, Any]:
    """
    Close the active Rhino document.

    Parameters:
    - save_changes: Save before closing (default False).
    - save_path: Optional Save As path when save_changes is True.
    """
    try:
        rhino = get_rhino_connection()
        command_params = {"save_changes": save_changes}
        if save_path is not None:
            command_params["save_path"] = save_path

        result = rhino.send_command("close_file", command_params)
        return result
    except Exception as e:
        logger.error(f"Error closing file: {str(e)}")
        return {"error": str(e)}
