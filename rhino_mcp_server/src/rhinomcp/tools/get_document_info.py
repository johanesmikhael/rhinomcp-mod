from mcp.server.fastmcp import Context
from rhinomcp import get_rhino_connection, mcp, logger
from typing import Any, Dict

@mcp.tool()
def get_document_info(
    ctx: Context,
    detail: str = "inventory",
    limit: int = 100,
    offset: int = 0,
    include_bbox: bool = True,
    max_geometry_points: int = 64,
) -> Dict[str, Any]:
    """
    Get information about the current Rhino document.

    Parameters:
    - detail: "inventory" for id/name/type/layer/bbox, "summary" for compact descriptors,
      or "full" for the legacy per-object geometry payload.
    - limit: Maximum number of objects returned in this page.
    - offset: Object offset for pagination.
    - include_bbox: Include world axis-aligned bounding boxes in inventory/summary responses.
    - max_geometry_points: Point cap used by detail="full" for curve/polyline geometry.
    """
    try:
        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "detail": detail,
            "limit": limit,
            "offset": offset,
            "include_bbox": include_bbox,
            "max_geometry_points": max_geometry_points,
        }
        return rhino.send_command("get_document_info", params)
    except Exception as e:
        logger.error(f"Error getting document info from Rhino: {str(e)}")
        return {"error": str(e)}
