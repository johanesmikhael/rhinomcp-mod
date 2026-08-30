# Reading results

<!-- run: 2026-08-29, plugin 0.3.1 -->

| task | mcp | rhino command |
| --- | --- | --- |
| the verdict and every scalar | the `evaluate_stability` return value | the command line after `mcpmodevaluatestability` |
| what sections the stored report has | `get_stability_report()` | - |
| the most-tensioned joints | `get_stability_report(section="joint_forces", limit=10)` | the three printed after the verdict |
| joints of one type, by shear | `get_stability_report(section="joint_forces", joint_type="contact", sort="shear_n")` | - |
| joints that yielded | `get_stability_report(section="joint_forces", reached_capacity_only=True)` | - |
| the joints of one element | `get_stability_report(section="joint_forces", ids=[...])` | - |
| a node cluster and its resolved type | `get_stability_report(section="nodes")` | - |
| ground reactions | `get_stability_report(section="ground_sites")` | - |
| the sway figures | `get_stability_report(section="sway")` | printed when the probe was on |
| everything at once | `evaluate_stability(..., detail="full")` | - |

`evaluate_stability` returns a summary: the verdict, every scalar, a digest of the per-joint
forces and the ground reactions, and a list of what it left out. The complete report is
stored on the document under `rhinomcp-mod:stability-report` and read back a page at a time.
On the 104-element bridge the summary is 5 KB; the complete report is 112 KB, which is more
than a tool result carries into context.

## The summary

```python
result = evaluate_stability(mode="elements")
```

```text
{"success": true, "stable": true, "verdict": "stable", "conclusive": true, "diverged": false,
 "mode": "elements", "evaluation_mode": "multi_body_pinned_dynamic", "integrator": "rigid_bodies",
 "body_count": 104, "joint_count": 77, "joint_type_counts": {"contact": 24, "pin": 53, "fixed": 0},
 "joint_type_default": "contact", "joint_type_pair_rules": 2,
 "max_pin_displacement_m": 0.00305, "mechanism_threshold_m": 0.1365, "settled_displacement_m": 0.00123,
 "steps_run": 63216, "simulated_seconds": 0.5, "settled": false, "peak_speed_m_s": 0.118,
 "contact_joints_sided": 48, "contact_joints_open": 16, "joints_with_capacity": 0, "joints_at_capacity": 0,
 "total_mass_kg": 37231.4, "total_weight_n": 365115.4, "span_m": 27.29,
 "joint_forces_summary": {...}, "ground_sites_summary": {...},
 "detail": "summary", "omitted_sections": {"joint_forces": 266, "nodes": 59, "ground_sites": 24, ...},
 "report_key": "rhinomcp-mod:stability-report"}
```

| field | reads |
| --- | --- |
| `mode` | which of the two modes ran, `assembly` or `elements`. An older mode name resolves to one of them and says so in `unit_warnings` ([08](08-stability.md)) |
| `evaluation_mode` | the internal solver name, kept for callers that read it. `multi_body_pinned_dynamic` is the elements path; the "pinned" and "dynamic" in it name solvers that no longer exist separately, so read `mode` instead |
| `verdict` | `stable`, `unstable` or `inconclusive`. `inconclusive` is not `unstable`: the run ended before the assembly settled or clearly fell |
| `conclusive`, `diverged`, `diverged_reason` | whether the run answered at all; a diverged run stopped on a non-finite speed and reports no verdict |
| `max_pin_displacement_m` against `mechanism_threshold_m` | the verdict metric: the furthest any joint moved, against the distance that counts as collapse (a fraction of `span_m`) |
| `settled_displacement_m` | where the assembly came to rest - the elastic sag under its own weight |
| `steps_run`, `simulated_seconds`, `settled` | how long the run went and whether motion had stopped |
| `joint_type_counts`, `joint_type_default`, `joint_type_pair_rules` | what the joints were solved as, and how many rules did it |
| `contact_joints_sided`, `contact_joints_open` | bearings that carried load; bearings that lifted off |
| `joints_with_capacity`, `joints_at_capacity` | how many joints had a `capacity_kn`, and how many reached it |
| `sway` | present when `lateral_load_fraction` was set: stiffness along x and y in N/m, drift ratios, the softest direction |
| `omitted_sections` | each table left out, with its record count |

`joint_forces_summary`:

```text
{"count": 266, "max_force_n": 14125.5, "max_tension_n": 14108.7, "max_shear_n": 17626.8, "at_capacity": 0,
 "top_by_tension": [{"body": 43, "guid": "...", "with": [46, 47, 49, 59, 70, 99], "joint_type": "pin",
                     "force_n": 14125.5, "tension_n": 14108.7, "shear_n": 689.5, "capacity_n": null,
                     "reached_capacity": false, "bearing_points": 1}, ...],
 "top_by_force": [...],
 "at_capacity_joints": [...]}
```

Five joints in most tension, five most loaded, and any that yielded. `ground_sites_summary`
has the count of ground bearing points, how many opened, and the total, smallest and largest
vertical reaction.

## The report

```python
get_stability_report()                                              # sections and their sizes
get_stability_report(section="joint_forces", limit=10)              # highest tension first
get_stability_report(section="joint_forces", sort="shear_n", joint_type="pin")
get_stability_report(section="joint_forces", min_tension_n=5000)
get_stability_report(section="joint_forces", reached_capacity_only=True)
get_stability_report(section="joint_forces", ids=[guid])            # one element's joints
get_stability_report(section="nodes", ids=[guid])                   # the clusters it belongs to
get_stability_report(section="ground_sites", sort="fz_n", ascending=True)
get_stability_report(section="sway")                                # a scalar section, whole
get_stability_report(section="joint_forces", offset=20, limit=20)   # the next page
```

```text
{"success": true, "section": "joint_forces", "total": 266, "matched": 266, "offset": 0, "returned": 10,
 "sort": "tension_n", "ascending": false,
 "records": [{"body": 43, "guid": "...", "with": [46, ...], "joint_type": "pin", "bearing_points": 1,
              "force_n": 14125.5, "vector_n": [355.5, 92.9, -14108.7], "at_m": [10.0, 0.0, -0.04],
              "tension_n": 14108.7, "shear_n": 689.5, "capacity_n": null, "reached_capacity": false}, ...]}
```

Sections:

| section | one record per | sorted by default | filters |
| --- | --- | --- | --- |
| `joint_forces` | body per joint: the force that body receives there, its tension and shear across the bearing normal, capacity, which bodies share the joint | `tension_n` | `ids`, `joint_type`, `min_tension_n`, `reached_capacity_only` |
| `nodes` | joint cluster: member guids, diameter, centre, resolved `joint_type` and the `joint_type_rule` that decided it | `diameter_m` | `ids` (member) |
| `ground_sites` | ground bearing point: position, type, whether it opened, vertical reaction `fz_n` | `fz_n` | `joint_type` |
| `bodies` | element on the particle path: displacement and rotation | `displacement_m` | `ids` |
| `motion_samples_m`, `speed_samples_m_s`, `time_samples_s` | per-step traces | - | - |
| `sway`, or any scalar | returned whole under `value` | - | - |

`total` is the section's size, `matched` what survived the filters, `returned` the page.
`limit` is 1-500, default 20.

**Tension per point, not net.** `tension_n` is the net pull across a joint. A joint spread
over several bearing points can sit in net compression while one point is pulled hard; a
cantilever's connection at -7.1 kN net had a point at 24.5 kN. `peak_point_tension_n` on the
record is that point, and it is what a capacity is compared against.

**Body indices.** `body` and `with` are indices into the run's body list, and bodies are
numbered in the order the graph found them - the same model evaluated twice can number them
differently. Match on `guid`.

## Full detail

```python
evaluate_stability(mode="elements", detail="full")
```

Everything the report has, in the one answer, as before 0.3.1. For an assembly of a few
elements it fits; for a real one it does not reach the model.
