# Pose and transforms

<!-- run: 2026-08-29, plugin 0.3.1, RhinoAndGHFiles/guide_shapes.3dm -->

| task | mcp | rhino command |
| --- | --- | --- |
| read an object's pose | `get_object_info(name="TURNED_BLOCK")` | `What` |
| rotate about a pivot | `rotate_objects(objects=[{"id": ..., "rotation_matrix": R, "pivot": [0, 0, 0]}])` | `Rotate3D` |
| undo a rotation | `invert_rotation_matrix(rotation_matrix=R)`, then rotate by the inverse | `Undo` |
| put objects back where they started | `reset_objects_pose(objects=[{"id": ...}])` | - |
| take the current placement as the canonical one | `rebase_objects_pose(objects=[{"id": ...}])` | - |
| draw the boxes and profiles | - | `mcpmodobb` |
| forget the stored poses and boxes | - | `mcpmodclearcache` |

A pose is the frame an object's geometry is described in: `world_from_local` with a 3x3
rotation `R` and a translation `t`. It is what makes an oriented box meaningful - extents in
the object's own directions rather than in the world's - and it is how a turned member is
placed, measured and put back.

![The eight shapes with the OBB overlay on: a yellow oriented box round each solid, a coloured axis triple at each pose origin, and cyan projection profiles on the box faces](img/pose-obb.png)

## Where the pose comes from

Poses are detected from the geometry - the box that fits an extrusion most tightly, the plane
a polyline lies in - and stored on the object, so a second read is the same answer as the
first:

| user string | holds |
| --- | --- |
| `rhinomcp.pose.v1` | the pose, `R` and `t` |
| `rhinomcp.pose.mode.v1` | `detected` or `explicit` |
| `rhinomcp.obb.v1` | the oriented box that goes with it |

A transform through `modify_objects` or `rotate_objects` recomputes a `detected` pose, so the
box stays minimal. An `explicit` pose - one set by `rebase_objects_pose` - is carried through
the transform instead of recomputed: it stays the frame that was chosen, however the object
is moved afterwards. `mcpmodclearcache` deletes all three, and the next read detects again.

## Rotating

```python
rotate_objects(objects=[{
    "id": "...",
    "rotation_matrix": [[0.866, -0.5, 0.0], [0.5, 0.866, 0.0], [0.0, 0.0, 1.0]],
    "pivot": [0, 0, 0],
}])
```

```text
{"rotated": 1, "updates": [{"id": "a52346b7-...", "name": "BLOCK",
   "updated": {"pose": {"world_from_local": {"R": [[0.866019, -0.500011, 0.0],
                                                   [0.500011, 0.866019, -0.0],
                                                   [0.0, 0.0, 1.0]],
                                             "t": [-0.0, -0.0, 90.0]}},
               "position": [-0.0, -0.0, 90.0]},
   "changed_fields": ["pose", "position"]}]}
```

The matrix is about world axes; `pivot` is the world point it turns about and is required -
omit it and the call fails with `Missing pivot`. `all=True` applies one entry to every object.
`modify_objects` takes the same `rotation_matrix` but always turns about the object's own
bounding-box centre, so `rotate_objects` is the one to use when the pivot matters.

To undo one, invert it and rotate again about the same pivot:

```python
invert_rotation_matrix(rotation_matrix=[[0.866, -0.5, 0.0], [0.5, 0.866, 0.0], [0.0, 0.0, 1.0]])
# {"inverse_rotation_matrix": [[0.866, 0.5, 0.0], [-0.5, 0.866, 0.0], [0.0, 0.0, 1.0]]}
```

For a proper rotation the inverse is the transpose, which is what the tool returns. Passing
`invert_rotation_matrix=True` alongside a `rotation_matrix` in `rotate_objects` or
`modify_objects` does the same inline - give it an object's current pose `R` and the object
lands back on the world axes.

## Reset and rebase

```python
reset_objects_pose(objects=[{"id": "..."}])
```

```text
{"reset": 1, "updates": [{"id": "a52346b7-...", "name": "BLOCK",
   "updated": {"pose": {"world_from_local": {"R": [[1.0, 0.0, -0.0], [-0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
                                             "t": [0.0, 0.0, 0.0]}},
               "position": [0.0, 0.0, 0.0]},
   "changed_fields": ["pose", "position"]}]}
```

Reset moves the object: back to the identity rotation and to the origin. Per object,
`reset_rotation` and `reset_translation` (both default true) choose which half, and
`target_translation` gives a destination other than the origin - so
`{"reset_rotation": True, "reset_translation": False}` straightens an object without moving
it off its spot.

```python
rebase_objects_pose(objects=[{"id": "...", "z_direction": "+z", "x_direction": "+x"}])
```

Rebase moves nothing. It declares that where the object is now *is* its canonical pose, with
the translation anchored at the current bounding-box centre, and marks the pose `explicit` so
later transforms carry it rather than re-detecting it. The orientation is taken from the
object's current pose frame; `z_direction` (`+z`, `-z`) and `x_direction` (`+x`, `-x`, `+y`,
`-y`) resolve it to the nearest signed axis permutation, which is how to pin down which way
"up" and "along" are for a member whose detected frame is one of several equally tight ones.
Without the hints the frame that comes back can be a signed permutation of the one that went
in - the same box, named differently - so state them when the axes have to mean something.

The pair is a placement workflow: rebase a component in the orientation it is authored in,
transform it into position, and `reset_objects_pose` returns it to that authored placement
rather than to the world origin.

## The overlay

`mcpmodobb` draws what the stored poses and boxes say, for every object:

```text
mcpmodobb            with no option, toggles
mcpmodobb On
mcpmodobb Off
mcpmodobb Status     prints "MCP OBB is ON." or "MCP OBB is OFF."
```

`run_rhino_command("mcpmodobb On")` passes the option token. What is drawn: the oriented box
in yellow, the pose axes at its origin, and the projection profiles on the box faces - the
silhouettes `geometry_detail="ortho3"` returns ([02](02-scene-inspection.md)). It is the
check that a member's box is the tight one and its frame points along the member, before
anything is measured off it.

There is no MCP tool for the overlay; there is no Rhino command for reading or setting a
pose. This is one of the few features where the two routes do not meet in the middle.
