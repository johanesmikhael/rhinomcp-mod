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

`get_document_info` is the first call: it says what exists and gives the ids the other tools
take. It never returns geometry - that is `get_objects_info`, on the handful of ids that
turned out to matter.

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

`object_count` is the document; `objects_returned` is this page. `objects_truncated` true
means there are more - page with `offset`, or narrow the scope. Layers are listed the same
way and truncate the same way.

`units`, `mass_unit`, `density_unit` and `tolerance` are worth reading before anything else.
Every length in every other tool is in document units, and the tolerance is what contact
detection is measured in ([06](06-connectivity-graph.md)). Mass and density follow the document
too, which is why they are named here rather than left implied: `assign_mass(density=2400)`
means concrete in this millimetre document and sixteen times concrete in an imperial one, and
no later tool can tell the two apart ([07](07-mass-joint-types.md)).

Three levels of `detail`:

| detail | per object | when |
| --- | --- | --- |
| `inventory` (default) | id, name, type, layer, world bounding box | finding things |
| `summary` | the above plus point counts, planarity, face counts, material and colour | telling similar things apart |
| `full` | the legacy per-object payload, geometry included | rarely; prefer `get_objects_info` |

## A region rather than a document

```python
get_document_info(bbox=[[0, 0, 0], [600, 400, 400]], bbox_mode="intersects")
```

`bbox` is a world axis-aligned box, `[[min_x, min_y, min_z], [max_x, max_y, max_z]]`.
`bbox_mode` is `intersects` (touches the box), `contains_center` (its centre is inside) or
`contained` (all of it is inside). The response carries a `spatial_filter` block with the
normalised box, the mode, and how many objects matched.

On a large model this is the difference between a listing that is read and one that is paged
through: scope to the bay being worked on rather than raising `limit`.

## One object, and several

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

That block is the point of these two tools: a solid comes back as an oriented box - three
extents in its own frame - plus the frame itself, rather than as a world box that says a
turned beam is as wide as it is long. `R` and `t` are `world_from_local`: local to world.
What they mean and how to change them is [04](04-pose-transforms.md).

`geometry_detail` picks how much:

| geometry_detail | returns |
| --- | --- |
| `bbox` | the world axis-aligned box only |
| `obb_pose` (default) | oriented box extents plus pose; lines and curves as local points plus pose |
| `ortho3` | oriented box, pose, and up to three orthographic outlines |

`ortho3` is for shapes an oriented box cannot tell apart - a cone, a cylinder and a tapered
box with the same extents. It returns silhouettes on three planes of the local frame:

```python
get_objects_info(objects=[{"name": "CAP"}], geometry_detail="ortho3", outline_max_points=12)
```

```text
"views_frame": "local pose; top=[X,Y] front=[X,Z] right=[Y,Z]; shared origin; silhouette (direction-agnostic)",
"views": [{"axis": "top",   "loops": [[[87.27, 66.96], [109.06, -14.36], ...]]},
          {"axis": "front", "loops": [[[0.0, -120.0], [-110.0, 120.0], [110.0, 120.0], [0.0, -120.0]]]},
          {"axis": "right", "loops": [[[66.96, 120.0], [-110.0, 120.0], [0.0, -120.0], ...]]}]
```

The front outline is a triangle and the top outline a circle: a cone, apex at negative local
z. `outline_max_points` caps the points per loop. Non-solids fall back to `obb_pose`.

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
