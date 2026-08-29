# Views and capture

<!-- run: 2026-08-29, plugin 0.3.1, RhinoAndGHFiles/timber_bridge.3dm and timber_bridge_xbraced.3dm -->

| task | mcp | rhino command |
| --- | --- | --- |
| a picture of everything | `capture_view(all_visible=True)` | `ViewCaptureToFile` |
| a picture of some objects | `capture_view(ids=[...])`, `selected=True` | select, then `ViewCaptureToFile` |
| from a standard direction | `capture_view(view="front")` | `Front`, `Zoom Extents` |
| from a stated camera | `capture_view(camera_location=[...], camera_target=[...], lens_mm=85)` | `Viewport Properties` |
| in another display mode | `capture_view(display_mode="Technical")` | the mode's own command |
| bigger | `capture_view(resolution="print")`, `width=`, `height=` | `ViewCaptureToFile` |
| on white | `capture_view(background="white")` | - |
| what the viewports are doing | `get_viewport_info()` | `ViewportProperties` |
| zoom the real viewport | `zoom_to_objects(ids=[...])` | `Zoom Selected` |
| save, restore, delete a named view | `save_named_view(name=...)`, `restore_named_view(name=...)`, `delete_named_view(name=...)` | `NamedView` |
| list named views | `get_named_views()` | `NamedView` |

`capture_view` returns the PNG itself - base64 in `png_base64`, plus a `metadata` block - not
a path on disk. Nothing is written to a file unless the caller writes it. By default the
active viewport is put back exactly as it was afterwards.

## Framing

```python
capture_view(all_visible=True, view="isometric")
capture_view(ids=["...", "..."], view="top")
capture_view(selected=True, view="front", padding=1.4)
capture_view(view="perspective", fit=False)          # keep the camera where it is
```

Targets are `ids`, `selected=True` or `all_visible=True`; with none of them the current
viewport is captured as it stands. `view` is `perspective`, `isometric`, `top`, `front` or
`right` - the first two perspective, the rest parallel. `fit` (default true) frames the
targets, `padding` (default 1.15, minimum 1.0) leaves room around them.

An explicit camera overrides the preset. Both ends are required, and it is always
perspective:

```python
capture_view(all_visible=True, fit=False,
             camera_location=[-9000, -14000, 7000], camera_target=[9000, 0, 1500],
             lens_mm=85)
```

![The x-braced bridge seen from an 85 mm camera set close to one abutment, the trusses running away from the viewer](img/views-camera-explicit.png)

`lens_mm` is the 35 mm-equivalent focal length; the presets use 50 mm and never inherit a
lens from a parallel viewport. `camera_up` sets which way is up when the default roll is
wrong. `fit=False` keeps the stated camera from being overridden by the framing.

## Display modes and background

```python
capture_view(all_visible=True, view="front", display_mode="Technical")
```

![The portal bridge in the Technical display mode, seen from the front: hidden lines dashed, visible lines solid, the members drawn as line work](img/views-technical.png)

`display_mode` is any Rhino display mode name - `Shaded`, `Rendered`, `Wireframe`,
`Technical`, `Arctic`, `Ghosted`. `Rendered` is the one that shows assigned materials
([03](03-geometry-layers-materials.md)); `Shaded` shows the display mode's own object colour.

`background` is `viewport` (the display mode's own gradient, the default), `white` or
`transparent`. A display mode that fills objects with a white material - the default `Shaded`
on macOS is one - leaves nothing but edges on a white background; either change the mode's
object colour or shoot `Arctic`, which shades white surfaces.

`draw_grid` and `draw_axes` are off by default, so a capture is the model and not the
construction plane.

Display conduits are drawn in every case: the connectivity graph overlay
([06](06-connectivity-graph.md)), the settled pose ([08](08-stability.md)) and the OBB
overlay ([04](04-pose-transforms.md)) all appear in a capture if they are on.

## Size

| resolution | pixels |
| --- | --- |
| `low` | 640 x 480 |
| `medium` | 960 x 720 (default) |
| `high` | 1280 x 900 |
| `print` | 2560 x 1800 |

`width` and `height` override the preset, clamped to 256..3840 and to about 8.3 megapixels
in total. Screen-space items - overlay text, node markers, line widths - scale with the
capture, so a `print` capture is a larger picture rather than the same picture with unreadable
labels. The PNG travels back over the socket as base64: at `print` size that is roughly a
megabyte, which is fine for a file and large for a chat.

## What comes back

```text
{"png_base64": "iVBORw0KGgo...",
 "metadata": {"view": "front", "target_mode": "all_visible", "display_mode": "Technical",
              "width": 2560, "height": 1800,
              "camera_location": [12000.0, -3730.635217, 1520.0],
              "camera_target": [12000.0, 0.0, 1520.0], "camera_up": [0.0, 0.0, 1.0],
              "lens_mm": null, "projection": "parallel", "preserve_view": true,
              "object_count": 90, "objects": [{"id": "...", "name": "BOT_0_00", "type": "Brep"}, ...],
              "bbox": {"min": [-1500.0, -2000.0, -800.0], "max": [25500.0, 2000.0, 3840.0]}}}
```

The camera fields are what was used, so a capture that framed itself can be reproduced
exactly: pass them back as `camera_location` / `camera_target` / `lens_mm` with `fit=False`.
`lens_mm` is null for a parallel projection.

`preserve_view` defaults to true: camera, projection, lens, frustum and display mode are
restored after the capture, so the person at Rhino sees no change. Set it false only when the
capture is meant to become the new view.

## The real viewports

```python
get_viewport_info()
```

```text
{"viewports": [{"id": "1dd48cc7-...", "name": "Perspective", "active": true,
                "projection": "perspective", "lensMm": 50.0, "displayMode": "Shaded",
                "cameraLocation": "2632.94,-1932.94,1901.41",
                "cameraTarget": "904.17,-204.17,777.71"},
               {"name": "Top", "active": false, "projection": "parallel",
                "lensMm": null, "displayMode": "Wireframe", ...}]}
```

`displayMode` here is worth a look before capturing: the capture switches the mode itself, but
a viewport left in `Wireframe` is what the person at Rhino is looking at.

```python
zoom_to_objects(ids=["..."])     # or nothing, for the current selection
```

`zoom_to_objects` changes the real viewport and leaves it changed - it is the tool for "show
me that", where `capture_view` is the tool for "let me see it".

## Named views

```python
save_named_view(name="guide iso")        # the active viewport's camera, or viewport="Front"
get_named_views()
restore_named_view(name="guide iso")     # into the active viewport, or viewport=
delete_named_view(name="guide iso")
```

```text
{"named_views": [{"index": 0, "name": "guide iso", "projection": "perspective", "lensMm": 50.0,
                  "cameraLocation": "2632.94,-1932.94,1901.41",
                  "cameraTarget": "904.17,-204.17,777.71"}], "count": 1}
```

Named views are stored in the document and saved with the file, so they survive a restart -
unlike saved layer states, which live in the plugin for the session
([03](03-geometry-layers-materials.md)). Saving over an existing name replaces it, so a name
stays unambiguous to restore by.
