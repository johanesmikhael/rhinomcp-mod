from mcp.server.fastmcp import Context
from rhinomcp import get_rhino_connection, mcp, logger
from typing import Any, Dict, List, Optional

@mcp.tool()
def get_document_info(
    ctx: Context,
    detail: str = "inventory",
    limit: int = 100,
    offset: int = 0,
    include_bbox: bool = True,
    max_geometry_points: int = 64,
    bbox: Optional[List[List[float]]] = None,
    bbox_mode: str = "intersects",
) -> Dict[str, Any]:
    """
    Get information about the current Rhino document.

    `meta_data` carries the units every other tool works in: `units` for lengths,
    and `mass_unit` / `density_unit` for the numbers assign_mass takes - kg and
    kg/m³ in a metric document, lbm and lbm/ft³ in an imperial one. Read them
    before stating a mass or a density; a bare 2400 is concrete in one document
    and sixteen times concrete in the other, and nothing downstream can tell
    which was meant.

    Parameters:
    - detail: "inventory" for id/name/type/layer/bbox, "summary" for compact descriptors,
      or "full" for the legacy per-object geometry payload.
    - limit: Maximum number of objects returned in this page.
    - offset: Object offset for pagination.
    - include_bbox: Include world axis-aligned bounding boxes in inventory/summary responses.
    - max_geometry_points: Point cap used by detail="full" for curve/polyline geometry.
    - bbox: Optional world axis-aligned bounding box filter:
      [[min_x, min_y, min_z], [max_x, max_y, max_z]].
    - bbox_mode: Spatial filter mode: "intersects", "contains_center", or "contained".
    """
    try:
        rhino = get_rhino_connection()
        params: Dict[str, Any] = {
            "detail": detail,
            "limit": limit,
            "offset": offset,
            "include_bbox": include_bbox,
            "max_geometry_points": max_geometry_points,
            "bbox_mode": bbox_mode,
        }
        if bbox is not None:
            params["bbox"] = bbox
        return rhino.send_command("get_document_info", params)
    except Exception as e:
        logger.error(f"Error getting document info from Rhino: {str(e)}")
        return {"error": str(e)}
