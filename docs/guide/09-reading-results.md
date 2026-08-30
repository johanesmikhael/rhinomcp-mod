# Reading results

<!-- run: 2026-08-30, plugin 0.4.0 -->

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

`evaluate_stability` returns a summary containing the verdict, scalar results, condensed
joint-force and ground-reaction data, and a list of omitted sections. The complete report is
stored on the document under `rhinomcp-mod:stability-report` and can be read one page at a
time. For the 104-element bridge, the summary is 5 KB and the complete report is 112 KB, which
is generally too large for a single tool response.

## The summary

```python
result = evaluate_stability(mode="elements")
```

```text
{"success": true, "stable": true, "verdict": "stable", "conclusive": true, "diverged": false,
 "mode": "elements", "integrator": "rigid_bodies",
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

| field | description |
| --- | --- |
| `mode` | mode used for the evaluation: `assembly` or `elements` ([08](08-stability.md)) |
| `verdict` | `stable`, `unstable`, or `inconclusive`; `inconclusive` means the run ended before the assembly settled or clearly fell |
| `conclusive`, `diverged`, `diverged_reason` | whether the run produced a verdict; a diverged run stops on a non-finite speed and reports the reason |
| `max_pin_displacement_m` against `mechanism_threshold_m` | the verdict metric: the furthest any joint moved, against the distance that counts as collapse (a fraction of `span_m`) |
| `settled_displacement_m` | where the assembly came to rest - the elastic sag under its own weight |
| `steps_run`, `simulated_seconds`, `settled` | how long the run went and whether motion had stopped |
| `joint_type_counts`, `joint_type_default`, `joint_type_pair_rules` | joint types used, the default type, and the number of pair rules applied |
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

The summary includes the five joints with the highest tension, the five with the highest total
force, and all joints that yielded. `ground_sites_summary` reports the number of ground bearing
points, the number that opened, and the total, minimum, and maximum vertical reactions.

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

**Point tension and net tension.** `tension_n` is the net pull across a joint. A joint spread
over several bearing points can be in net compression while one point carries high tension.
For example, a cantilever connection at -7.1 kN net compression had one point at 24.5 kN
tension. `peak_point_tension_n` reports that value and is used for the capacity comparison.

**Body indices.** `body` and `with` are indices into the run's body list. Numbering follows the
graph traversal and can differ between evaluations of the same model. Use `guid` for stable
matching.

## Full detail

```python
evaluate_stability(mode="elements", detail="full")
```

This returns every report section in one response, matching the behaviour before version 0.3.1.
Use it for small assemblies. For larger models, page through the stored report to avoid an
oversized tool response.
