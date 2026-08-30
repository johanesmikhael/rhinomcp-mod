# Scene inspection

<!-- run: 2026-08-29, plugin 0.3.1, RhinoAndGHFiles/guide_shapes.3dm -->

| task | mcp | rhino command |
| --- | --- | --- |
| what is in the document | `get_document_info()` | `SelAll` then `List` |
| the next page of it | `get_document_info(offset=100)` | - |
| only what is in a region | `get_document_info(bbox=[[0,0,0],[600,400,400]])` | - |
| one object in detail | `get_object_info(name="TURNED_BLOCK")` | `What` |
| several objects in detail | `get_objects_info(objects=[{"name": "CAP"}])` | `What` |
| what is selected | `get_selected_objects()` | `SelList` |
| select by layer, name, id or type | `select_objects(layer="SHAPES")` | `SelLayer`, `SelName` |
| clear the selection | `deselect_all()` | `SelNone` |

Start with `get_document_info`. It returns the document inventory and the object ids accepted
by other tools. It does not return geometry; use `get_objects_info` for the relevant objects.

## The inventory

```python
get_document_info()                                  # first 100 objects, with bounding boxes
get_document_info(limit=500, offset=500)             # the next page
get_document_info(include_bbox=False)                # ids, names, types and layers only
get_document_info(detail="summary")                  # plus point counts, planarity, face counts, colour
get_document_info(detail="full", max_geometry_points=64)   # the legacy per-object payload
```

```text
{"meta_data": {"name": "guide_shapes.3dm", "tolerance": 0.001, "angle_tolerance": 1.0,
               "path": "/.../RhinoAndGHFiles/guide_shapes.3dm", "units": "Millimeters",
               "mass_unit": "kg", "density_unit": "kg/m³"},
 "detail": "inventory", "object_count": 8, "objects_returned": 3, "objects_offset": 0,
 "objects_limit": 3, "objects_truncated": true, "objects_skipped_errors": 0,
 "objects": [{"id": "004b1f31-...", "name": "POST", "type": "Brep", "layer": "SHAPES",
              "bbox": [[790.0, -70.0, 0.0], [930.0, 70.0, 300.0]], "bbox_frame": "world_aabb"}, ...],
 "layer_count": 1, "layers": [{"id": "92bc2989-...", "name": "SHAPES", "visible": true, "locked": false}]}
```

`object_count` is the number of objects in the document, and `objects_returned` is the number
on the current page. If `objects_truncated` is true, use `offset` to request another page or
narrow the scope. Layer results use the same paging behaviour.

Check `units`, `mass_unit`, `density_unit`, and `tolerance` before making geometry or mass
calls.
Every other tool reports length in document units, and contact detection uses the document
tolerance ([06](06-connectivity-graph.md)). Mass and density inputs also follow document units.
For example, `assign_mass(density=2400)` represents concrete in this millimetre document but
2400 lbm/ft³, about sixteen times the density of concrete, in an imperial document. Later tools
cannot infer which unit was intended ([07](07-mass-joint-types.md)).

Three levels of `detail`:

| detail | per object | when |
| --- | --- | --- |
| `inventory` (default) | id, name, type, layer, world bounding box | finding things |
| `summary` | the above plus point counts, planarity, face counts, material and colour | telling similar things apart |
| `full` | the legacy per-object payload, geometry included | rarely; prefer `get_objects_info` |

<a id="a-region-rather-than-a-document"></a>

## Inspecting a region

```python
get_document_info(bbox=[[0, 0, 0], [600, 400, 400]], bbox_mode="intersects")
```

`bbox` is a world axis-aligned box, `[[min_x, min_y, min_z], [max_x, max_y, max_z]]`.
`bbox_mode` is `intersects` (touches the box), `contains_center` (its centre is inside) or
`contained` (all of it is inside). The response carries a `spatial_filter` block with the
normalised box, the mode, and how many objects matched.

For a large model, use a bounding-box scope for the region of interest instead of increasing
`limit` for the entire document.

<a id="one-object-and-several"></a>

## Inspecting individual objects

```python
get_object_info(name="TURNED_BLOCK")                       # or id=
get_objects_info(objects=[{"name": "CAP"}, {"id": "..."}])
```

```text
{"id": "fbf11fc9-...", "name": "TURNED_BLOCK", "type": "EXTRUSION", "layer": "SHAPES",
 "material": "5", "color": {"r": 176, "g": 176, "b": 176},
 "geometry": {"obb": {"extents": [600.01, 240.0, 200.0]},
              "pose": {"world_from_local": {"R": [[0.866025, -0.5, 0.0],
                                                  [0.5, 0.866025, -0.0],
                                                  [0.0, 0.0, 1.0]],
                                            "t": [520.0, 700.0, 100.0]}}}}
```

These tools describe a solid with an oriented box and its coordinate frame. The three extents
are measured in the object's local frame, so a rotated beam is not represented by an oversized
world-aligned box. `R` and `t` define `world_from_local`, the transform from local to world
coordinates. See [04](04-pose-transforms.md) for pose operations.

`geometry_detail` picks how much:

| geometry_detail | returns |
| --- | --- |
| `bbox` | the world axis-aligned box only |
| `obb_pose` (default) | oriented box extents plus pose; lines and curves as local points plus pose |
| `ortho3` | oriented box, pose, and up to three orthographic outlines |

Use `ortho3` to distinguish shapes that have the same oriented-box extents, such as a cone,
cylinder, and tapered box. It returns silhouettes on three planes of the local frame:

```python
get_objects_info(objects=[{"name": "CAP"}], geometry_detail="ortho3", outline_max_points=12)
```

```text
"views_frame": "local pose; top=[X,Y] front=[X,Z] right=[Y,Z]; shared origin; silhouette (direction-agnostic)",
"views": [{"axis": "top",   "loops": [[[87.27, 66.96], [109.06, -14.36], ...]]},
          {"axis": "front", "loops": [[[0.0, -120.0], [-110.0, 120.0], [110.0, 120.0], [0.0, -120.0]]]},
          {"axis": "right", "loops": [[[66.96, 120.0], [-110.0, 120.0], [0.0, -120.0], ...]]}]
```

In this response, the triangular front outline and circular top outline identify a cone with
its apex in the negative local z direction. `outline_max_points` limits the points per loop.
Non-solid objects fall back to `obb_pose`.

`include_attributes=True` adds the user attributes on each object - that is where mass and
element joint type are stored ([07](07-mass-joint-types.md)). `include_world=True` adds
world-space duplicates (`world_points`, `world_start`/`world_end`, `obb.world_corners`) next
to the local ones; without it the response carries each coordinate once.

## Selection

```python
select_objects(type="Extrusion")            # Brep, Mesh, Curve, Extrusion, Point, PointSet, Annotation, Hatch, Light, SubD
select_objects(layer="SHAPES")
select_objects(names=["BLOCK", "CAP"])      # exact names
select_objects(ids=["..."])
get_selected_objects()
deselect_all()
```

Filters are OR: anything matching any of them is selected. The selection is what
`selected=True` means to `capture_view` ([05](05-views-capture.md)),
`get_connectivity_graph` ([06](06-connectivity-graph.md)) and `assign_mass`
([07](07-mass-joint-types.md)), and it is what a Rhino command with a pre-selection acts on -
which is how a scope is handed from the MCP route to the command route.

```text
Selected 2 object(s):
  - BLOCK (Extrusion) on layer 'SHAPES'
  - TURNED_BLOCK (Extrusion) on layer 'SHAPES'
```
