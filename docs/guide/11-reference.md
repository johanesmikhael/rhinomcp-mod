# Reference

Every MCP tool and every Rhino command, with its counterpart on the other route. `-` means the
other route has no equivalent.

## MCP tools

| tool | does | rhino command | page |
| --- | --- | --- | --- |
| `get_document_info` | document metadata and an object listing at `detail="inventory"`, `"summary"` or `"full"`; `limit`/`offset` paging; `bbox` + `bbox_mode` scope | - | [02](02-scene-inspection.md) |
| `get_object_info` | one object by `id` or `name`; `geometry_detail` as below | `What` | [02](02-scene-inspection.md) |
| `get_objects_info` | several objects by selectors; `geometry_detail="bbox"` (world box), `"obb_pose"` (oriented box + pose), `"ortho3"` (three outlines); `include_attributes`, `include_world` | - | [02](02-scene-inspection.md) |
| `get_selected_objects` | id, name, type, layer of the current selection | - | [02](02-scene-inspection.md) |
| `select_objects` | select by `ids`, `names`, `layer` or `type` (any match) | `SelLayer`, `SelName` | [03](03-geometry-layers-materials.md) |
| `deselect_all` | clear the selection | `SelNone` | [03](03-geometry-layers-materials.md) |
| `create_objects` | create points, lines, polylines, curves, circles, arcs, ellipses, boxes, spheres, cones, cylinders, surfaces; optional `translation`, `rotation`, `scale`, `color`, `name` | the drawing commands | [03](03-geometry-layers-materials.md) |
| `copy_objects` | copy by id or name with a translation | `Copy` | [03](03-geometry-layers-materials.md) |
| `modify_objects` | rename, recolour, move to a layer, translate, rotate, scale, show or hide; `all=True` for every object | `Properties`, `Move`, `Scale` | [03](03-geometry-layers-materials.md) |
| `delete_objects` | delete by `ids` or `names`; `confirm=True` required | `Delete` | [03](03-geometry-layers-materials.md) |
| `create_layer` | new layer with `name`, optional `color` and `parent` | `Layer` | [03](03-geometry-layers-materials.md) |
| `delete_layer` | delete by `guid` or `name` | `Layer` | [03](03-geometry-layers-materials.md) |
| `rename_layer` | rename by `id` | `Layer` | [03](03-geometry-layers-materials.md) |
| `get_or_set_current_layer` | read the current layer, or set it by `guid` or `name` | `Layer` | [03](03-geometry-layers-materials.md) |
| `move_objects_to_layer` | move `ids` to `layer` | `ChangeLayer` | [03](03-geometry-layers-materials.md) |
| `get_layer_states` | visibility, lock and colour of every layer | `Layer` | [03](03-geometry-layers-materials.md) |
| `save_layer_state` | remember the current layer visibility and locks under `name`; in memory, gone when the plugin unloads | `LayerStateManager` | [03](03-geometry-layers-materials.md) |
| `restore_layer_state` | apply a saved state | `LayerStateManager` | [03](03-geometry-layers-materials.md) |
| `get_materials` | every material in the document | `Materials` | [03](03-geometry-layers-materials.md) |
| `create_material` | a material with a diffuse colour | `Materials` | [03](03-geometry-layers-materials.md) |
| `set_object_material` | assign by `material_name` or `material_index` | `Properties` | [03](03-geometry-layers-materials.md) |
| `get_object_materials` | the material on each of `ids` | `Properties` | [03](03-geometry-layers-materials.md) |
| `rotate_objects` | rotate about a pivot by a 3x3 matrix, per object or `all=True` | `Rotate3D` | [04](04-pose-transforms.md) |
| `invert_rotation_matrix` | the inverse of a 3x3 rotation, to undo one | - | [04](04-pose-transforms.md) |
| `rebase_objects_pose` | take the current placement as the canonical pose without moving anything | - | [04](04-pose-transforms.md) |
| `reset_objects_pose` | return objects to their canonical pose | - | [04](04-pose-transforms.md) |
| `capture_view` | frame `ids`, the selection or everything, capture a PNG in a `display_mode` at a `resolution`, optional explicit camera and lens; the view is restored after | `ViewCaptureToFile` | [05](05-views-capture.md) |
| `get_viewport_info` | camera, projection, lens and display mode of every viewport | `ViewportProperties` | [05](05-views-capture.md) |
| `zoom_to_objects` | zoom the active viewport to `ids` | `Zoom Selected` | [05](05-views-capture.md) |
| `get_named_views` | the named views with their cameras | `NamedView` | [05](05-views-capture.md) |
| `save_named_view` | save the active camera as `name` | `NamedView` | [05](05-views-capture.md) |
| `restore_named_view` | restore `name` into a viewport | `NamedView` | [05](05-views-capture.md) |
| `delete_named_view` | delete `name` | `NamedView` | [05](05-views-capture.md) |
| `get_connectivity_graph` | nodes and edges for a scope, each edge with its contact point; `source` says whether it was computed or read from the document cache | `mcpmodgraph`, `mcpmodgraphexport` | [06](06-connectivity-graph.md) |
| `graph_display` | show or hide the graph overlay; pin its scope | `mcpmodgraph` | [06](06-connectivity-graph.md) |
| `assign_mass` | mass per object from `density` (kg/m³) and volume, or one `mass` (kg) each; scoped by `ids`, `names`, `layer`, `selected`; `overwrite=False` fills only what is missing | `mcpmodassignmass`, `mcpmodassignmissingmass`, `mcpmodmassfromlayerdensity` | [07](07-mass-joint-types.md) |
| `assign_joint_type` | a rule: `joint_type` for a layer pair, an id pair, an element, or a founded base (`with_ground=True`); `capacity_kn`; `clear=True`; `prune=True`; no arguments lists | `mcpmodassignjointtype` | [07](07-mass-joint-types.md) |
| `evaluate_stability` | evaluate the scope: `mode` welded / pinned / contact, `joint_type` default, solver parameters, `lateral_load_fraction` sway probe, `display`, `detail` | `mcpmodevaluatestability` | [08](08-stability.md) |
| `get_stability_report` | one section of the last evaluation's stored report, sorted, filtered and paged; no `section` lists them | - | [09](09-reading-results.md) |
| `open_file` | open a `.3dm`, optionally closing the current document | `Open` | [01](01-setup.md#9-session-tools) |
| `close_file` | close the active document, optionally saving | `Close` | [01](01-setup.md#9-session-tools) |
| `list_plugins` | loaded plugins | `PlugInManager` | [01](01-setup.md#9-session-tools) |
| `run_rhino_command` | run a command by name with option tokens; prompting commands must be given their answers or the dashed form | - | [01](01-setup.md#9-session-tools) |
| `get_rhino_log` | the last `lines` of the command line | - | [01](01-setup.md#9-session-tools) |

## Rhino commands

| command | does | prompts and options | scripted form | mcp tool | page |
| --- | --- | --- | --- | --- | --- |
| `mcpmodstart` | start the listener on 1999 | none | `mcpmodstart` | - | [01](01-setup.md) |
| `mcpmodstop` | stop the listener | none | `mcpmodstop` | - | [01](01-setup.md) |
| `mcpmodversion` | print the loaded plugin version | none | `mcpmodversion` | `list_plugins` | [01](01-setup.md) |
| `mcpmodgraph` | build the graph for the selection and draw the overlay | pick objects, or `All` / `Off` | `mcpmodgraph All`, `mcpmodgraph Off` | `graph_display`, `get_connectivity_graph` | [06](06-connectivity-graph.md) |
| `mcpmodgraphexport` | write the graph to a JSON file | `Graph JSON output path` | `mcpmodgraphexport <path>` | `get_connectivity_graph` | [06](06-connectivity-graph.md) |
| `mcpmodassignmass` | mass per selected object | pick objects; a number per object, in kg (lbm in an imperial document) | - | `assign_mass(mass=...)` | [07](07-mass-joint-types.md) |
| `mcpmodassignmissingmass` | mass for objects that have none | a number per object | - | `assign_mass(overwrite=False)` | [07](07-mass-joint-types.md) |
| `mcpmodassignlayerdensity` | store a density on a layer | a number, kg/m³ (lbm/ft³ in an imperial document) | - | - | [07](07-mass-joint-types.md) |
| `mcpmodmassfromlayerdensity` | mass from each layer's density and each object's volume | none | `mcpmodmassfromlayerdensity` | `assign_mass(density=...)` | [07](07-mass-joint-types.md) |
| `mcpmodassignjointtype` | write, list, prune or clear joint rules | pick one side (or `List` / `Prune`), the other side, `Layers` / `Objects`, `Contact` / `Pin` / `Fixed` / `Clear` | - | `assign_joint_type` | [07](07-mass-joint-types.md) |
| `mcpmodevaluatestability` | run the evaluation | scope (pick, `All`, `Pinned`), `Assembly` / `Elements`, default joint type, `Defaults` / `Custom`, floor `Auto` / `Manual`, numbers, display `On` / `Off` | - | `evaluate_stability` | [08](08-stability.md) |
| `mcpmodstabilitydisplay` | show or hide the settled pose | `On` / `Off` | `mcpmodstabilitydisplay Off` | `evaluate_stability(display=...)` | [08](08-stability.md) |
| `mcpmodclearcache` | clear the stored graph, poses, boxes, masses and settled poses | `All` / `Selected` | `-mcpmodclearcache` | - | [00](00-overview.md#what-is-stored-on-the-document) |
| `mcpmodobb` | draw oriented bounding boxes and projection profiles | `On` / `Off` / `Toggle` / `Status` | `mcpmodobb Off` | - | [04](04-pose-transforms.md) |

Scripted form: what to pass to `run_rhino_command`. A command with `-` there prompts for a pick
and has no non-interactive path; use the tool.
