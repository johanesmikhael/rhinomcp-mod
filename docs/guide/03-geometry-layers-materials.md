# Geometry, layers, materials

<!-- run: 2026-08-29, plugin 0.3.1, RhinoAndGHFiles/guide_shapes.3dm -->

| task | mcp | rhino command |
| --- | --- | --- |
| create geometry | `create_objects(objects=[{"type": "BOX", ...}])` | the drawing commands |
| copy | `copy_objects(objects=[{"id": ..., "translation": [0, -400, 0]}])` | `Copy` |
| rename, recolour, move, scale, hide | `modify_objects(objects=[{...}])` | `Properties`, `Move`, `Scale` |
| delete | `delete_objects(names=["POST"], confirm=True)` | `Delete` |
| new layer | `create_layer(name="GUIDE", color=[200, 80, 40])` | `Layer` |
| rename or delete a layer | `rename_layer(id=..., new_name=...)`, `delete_layer(name=...)` | `Layer` |
| read or set the current layer | `get_or_set_current_layer(name="GUIDE")` | `Layer` |
| move objects to a layer | `move_objects_to_layer(ids=[...], layer="GUIDE")` | `ChangeLayer` |
| layer visibility and locks | `get_layer_states()` | `Layer` |
| remember and restore them | `save_layer_state(name="all on")`, `restore_layer_state(name="all on")` | `LayerStateManager` |
| materials in the document | `get_materials()` | `Materials` |
| a new material | `create_material(name="guide oak", r=190, g=150, b=96)` | `Materials` |
| assign one | `set_object_material(ids=[...], material_index=0)` | `Properties` |
| what is assigned | `get_object_materials(ids=[...])` | `Properties` |

![Six primitives from create_objects - a box, a sphere, a cylinder, a cone, a turned box and a cylinder laid along x - with a polyline and a circle, all rendered in one diffuse material](img/geometry-primitives.png)

That is `RhinoAndGHFiles/guide_shapes.3dm`, built by
[`scripts/dev/build_guide_images.py --build-shapes`](../../scripts/dev/build_guide_images.py)
out of the calls on this page.

## Creating

One call creates many objects. Each entry is a `type`, a `params` block for that type, and
optional placement and attributes:

```python
create_objects(objects=[
    {"type": "BOX", "name": "BLOCK", "params": {"width": 400, "length": 260, "height": 180},
     "translation": [0, 0, 90], "color": [176, 176, 176]},
    {"type": "CYLINDER", "name": "LAID_POST",
     "params": {"radius": 90, "height": 700, "cap": True, "axis": "x"},
     "translation": [1140, 700, 90]},
    {"type": "POLYLINE", "name": "PATH",
     "params": {"points": [[-200, 320, 0], [1340, 320, 0], [1340, 320, 400]]}},
])
```

| type | params |
| --- | --- |
| `POINT` | `x`, `y`, `z` |
| `LINE` | `start`, `end` |
| `POLYLINE` | `points` |
| `CURVE` | `points`, `degree` |
| `CIRCLE` | `center`, `radius` |
| `ARC` | `center`, `radius`, `angle` (degrees) |
| `ELLIPSE` | `center`, `radius_x`, `radius_y` |
| `BOX` | `width` (x), `length` (y), `height` (z) |
| `SPHERE` | `radius` |
| `CYLINDER` | `radius`, `height`, `cap`, `axis` (`x`, `y` or `z`) |
| `CONE` | `radius`, `height`, `cap`, `axis` |
| `SURFACE` | `points`, `count` as `[u_count, v_count]` |

Solids are built centred on the origin - a box on `Plane.WorldXY`, a cylinder and a cone
with their axis midpoint there - and then placed. So `translation` is where the centre goes,
not a corner: the 180 mm box above sits on z = 0 because it is lifted by half its height.
`rotation_matrix` (3x3, world axes), `scale` and `color` apply at creation;
`name` is what the response is keyed by, and what `names=` selectors match later.

Curves and circles are created on the world plane. Everything lands on the current layer.

## Copying, changing, deleting

```python
copy_objects(objects=[{"id": "...", "translation": [0, -400, 0]}])   # {"copied": 1}
modify_objects(objects=[{"id": "...", "new_name": "BALL2", "translation": [0, -300, 0]}])
delete_objects(names=["RING"], confirm=True)      # {"count": 1}
```

`modify_objects` takes `new_name`, `new_color`, `layer`, `translation`, `rotation_matrix`
(pivot at the object's bounding-box centre), `invert_rotation_matrix`, `scale` and `visible`,
per object, and `all=True` to apply one entry to every object. It reports what actually
changed:

```text
{"modified": 1, "updates": [{"id": "a191d7bf-...", "name": "BALL2",
   "updated": {"pose": {"world_from_local": {"R": [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
                                             "t": [520.0, -300.0, 110.0]}},
               "position": [520.0, -300.0, 110.0], "name": "BALL2"},
   "changed_fields": ["position", "pose", "name"]}]}
```

`delete_objects` requires `confirm=True`, and `names` must be unique in the document -
two objects called `POST` and it refuses rather than guessing:

```text
Multiple objects with name POST found.
```

A transform through `modify_objects` or `rotate_objects` recomputes the object's detected
pose, so its oriented box stays minimal; a pose that was set deliberately with
`rebase_objects_pose` is kept instead ([04](04-pose-transforms.md)).

## Layers

```python
create_layer(name="GUIDE", color=[200, 80, 40])   # {"id": "00f90bef-...", "name": "GUIDE", ...}
get_or_set_current_layer(name="GUIDE")            # sets it; no argument reads it
move_objects_to_layer(ids=["..."], layer="GUIDE") # {"message": "Moved 1 object(s) to layer.", "count": 1}
rename_layer(id="00f90bef-...", new_name="GUIDE 2")
delete_layer(name="GUIDE 2")
```

`create_layer` takes an optional `parent` layer name. `delete_layer` and
`get_or_set_current_layer` take either `name` or `guid`; `rename_layer` takes the id only.

Layers matter beyond tidiness here: mass is assigned by layer density and joint-type rules
are stated per layer pair ([07](07-mass-joint-types.md)), so the layer an element is on is
usually what decides how it is solved.

```python
get_layer_states()
```

```text
{"layers": [{"index": 0, "name": "SHAPES", "visible": true, "locked": false, "color": "0,0,0"}]}
```

```python
save_layer_state(name="all on")
restore_layer_state(name="all on")
```

Saved layer states live in the plugin, not in the document: they are gone when Rhino or the
plugin restarts. Named views are the opposite - they are stored in the `.3dm`
([05](05-views-capture.md)).

## Materials

```python
material = create_material(name="guide oak", r=190, g=150, b=96)   # {"message": ..., "index": 0}
set_object_material(ids=[...], material_index=0)
get_object_materials(ids=[...])
get_materials()
```

Only a diffuse colour is supported. `create_material` returns the index; assign by
`material_index` rather than by `material_name`, which has to look the name up and is
ambiguous when two materials share it.

```text
{"objects": [{"id": "2384a0d0-...", "name": "CAP", "material_index": 0, "material_name": "guide oak"},
             {"id": "4f743462-...", "name": "PATH", "material_index": -1, "material_name": "ByLayer"}],
 "count": 2}
```

`material_index` -1 is `ByLayer` - nothing assigned to the object itself. Materials show in a
capture taken in the `Rendered` display mode; `Shaded` uses the display mode's own colour
([05](05-views-capture.md)).
