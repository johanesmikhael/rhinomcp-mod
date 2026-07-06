from rhinomcp.server import mcp


# @mcp.prompt()
# def asset_general_strategy() -> str:
#     """Defines the preferred strategy for creating assets in Rhino"""
#     return """
    
#     QUERY STRATEGY:
#     - if the id of the object is known, use the id to query the object.
#     - if the id is not known, use the name of the object to query the object.


#     CREATION STRATEGY:

#     0. Before anything, always check the document from get_document_info().
#     1. Prefer create_objects() when creating multiple objects; use create_object() for single objects.
#     2. If there are multiple objects, use the method create_objects() to create multiple objects at once. Do not attempt to create them one by one if they are more than 10.
#     3. When including an object into document, ALWAYS make sure that the name of the object is meanful.
#     4. Try to include as many objects as possible accurately and efficiently. If the command is not able to include so many data, try to create the objects in batches.

#     When creating rhinoscript python code:
#     - do not hallucinate, only use the syntax that is supported by rhinoscriptsyntax or Rhino,Geometry.
#     - double check the code if any of the code is not correct, and fix it.
#     """


@mcp.prompt()
def asset_general_strategy() -> str:
    """General strategy: prefer controlled edits over uncontrolled duplication."""
    return """

    QUERY STRATEGY:
    - if the id of the object is known, use the id to query the object.
    - if the id is not known, use the name of the object to query the object.


    INTENT:
    - Prioritize stable, minimal-change modeling.
    - Default to modifying existing objects when that satisfies the request.
    - Avoid object-count explosion from unnecessary duplication.

    ALWAYS START:
    0) Before anything, check the scene with get_document_info(detail="inventory").
       Treat this as a compact scene index, not as a detailed geometry carrier.

    DEFAULT DESIGN OPERATORS:
    - Inspect and select target objects.
    - Transform existing targets:
    - translate
    - rotate
    - Use copy/repeat/pattern only when replication is explicitly needed.
    - Assemble configurations with the smallest required set of objects.

    TOOL USAGE GUIDELINES:
    - Use get_document_info(detail="inventory") for the first scene scan: id, name, type, layer, and world AABB bbox.
    - When the user names a focused region or you already know a target bbox, scope the scene scan with get_document_info(..., bbox=[[min_x,min_y,min_z],[max_x,max_y,max_z]], bbox_mode="intersects").
    - Use get_document_info(detail="summary") when compact per-object descriptors are needed without coordinate arrays.
    - Use limit/offset on get_document_info when the scene is large; watch objects_truncated and objects_returned.
    - Use get_object_info / get_objects_info to understand source objects.
    - Prefer get_objects_info for detailed geometry of selected targets after inventory/summary identifies them.
    - Use geometry_detail="bbox" for lean detail when only coarse location/size matters.
    - Use geometry_detail="obb_pose" for default detailed geometry; local coordinates + pose come by default, world duplicates only when include_world=true.
    - Use geometry_detail="ortho3" (solids/meshes) to disambiguate 3D shape when obb extents + one silhouette are ambiguous (cone vs cylinder vs tapered box share the same footprint and extents). Returns up to three orthographic outlines (top/front/right).
    - Avoid get_document_info(detail="full") unless the user explicitly needs legacy full per-object document payloads.
    - Use modify_object(s) first for direct edits.
    - Use copy_object(s) only when:
    - the user asks for duplication/patterning, or
    - preserving an original while creating variants is explicitly required.
    - Prefer batch operations when the operation is intentionally applied to many objects.

    CREATION RULE:
    - Create new geometry only when:
    - no suitable existing object can be modified to satisfy intent, or
    - the user explicitly asks for new objects.
    - When creating new objects, keep them minimal and purposeful.
    - If creating geometry at an arbitrary pose, plan the sequence of creation + translation + rotation (and scale if needed) before issuing tool calls.
    - After creation, verify the object matches the intended pose and dimensions; adjust with a corrective transform if needed.

    SAFETY:
    - Do not duplicate objects unless duplication is explicitly justified.
    - Before copy/repeat/pattern, check whether a direct modification already satisfies the request.
    - Do not hallucinate unsupported tool behavior.
    - Always ask for confirmation for delete operation.

    GEOMETRY PROTOCOL (OBB + POSE):
    - geometry_detail="bbox" returns only world AABB bbox and does not include pose.
    - geometry_detail="obb_pose" is default detailed mode.
    - The oriented bounding box (OBB) is defined in a local frame whose origin is the OBB center.
    - geometry.obb.extents = [x_len, y_len, z_len] are full side lengths in that local frame (not half-extents).
    - Local box corners are at (±x_len/2, ±y_len/2, ±z_len/2).
    - geometry.pose.world_from_local defines the local→world transform:
      - R is a 3x3 rotation matrix.
      - t is the world position of the OBB center (the local origin).
    - geometry.obb.world_corners are optional absolute world-space points when include_world=true and must be consistent with pose + extents.

    ORTHO3 PROTOCOL (geometry_detail="ortho3"):
    - Returns up to 3 orthographic outline views in the pose local frame, each {axis, loops}. loops is a list of closed outlines (usually one; more when the silhouette splits into disjoint parts), each a ring of 2D [u,v] pairs.
    - geometry.views_frame states the axis mapping: top=[X,Y], front=[X,Z], right=[Y,Z]. All views share the pose origin t and use the same units as obb.extents, so they are aligned like an engineering drawing (top's u-extent == front's u-extent == the object's X size).
    - geometry.views_dropped maps a dropped view tag to a kept one, e.g. {"right":"front"} means the right silhouette is ~identical to front (object is symmetric across that pair). It is NOT missing data.
    - Silhouettes are direction-agnostic: top==bottom, front==back, right==left. Each loop is closed; edge-on/degenerate planes are dropped. Multiple loops in one view mean disjoint parts (separate lumps), NOT holes: inner loops contained in a larger loop are dropped (a washer reads as a solid disc). Tiny specks below 5% of the largest loop's area are dropped.

    POSE CONVENTIONS (R, t):
    - R columns are the local X, Y, Z axes expressed in world coordinates (right-handed).
    - t is the world-space origin of the local frame.
    - LINE pose: local X is along the line (start → end), origin at midpoint. Y/Z are chosen to be orthonormal with a stable "up":
      - Use world Z as up unless the line is near-parallel to Z; then use world Y.
    - Planar CURVE/POLYLINE/BREP pose: a working plane is selected, origin set to the local bounds center.
      - Deterministic axis flips are applied:
        - If plane Z points opposite world Z, the plane is flipped.
        - If plane X points opposite world X, X and Y are flipped together.
    - Non-planar CURVE/POLYLINE pose: R is identity, t is the world-space bbox center.
    - Transforms: rotation_matrix/scale are applied about the bbox center using world axes; translation is world-space.

    SUMMARY:
    Query existing objects → modify with minimal changes → copy only when explicitly needed.
    """
