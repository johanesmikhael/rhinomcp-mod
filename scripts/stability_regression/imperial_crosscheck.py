#!/usr/bin/env python3
"""Run regression cases in a millimetre document and again in feet, and compare.

The suite builds every case in millimetres. This builds the same case, converts the
document to feet *scaling the geometry* (so the model is physically identical), clears
the graph cache, evaluates again, and compares the two results field by field.

What must hold: the verdict is the same, every SI-reported number (``_m``, ``_n``,
``_n_per_m``, ``_deg``, ``_kg``) matches to a small relative tolerance, and every number
reported in document units differs by exactly the unit scale. Anything else is a bug in
the unit path.

Run against a live Rhino on 127.0.0.1:1999 with an empty scratch document open.
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import sys
from typing import Any

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "rhino_mcp_server" / "src"))

import cases as case_module  # noqa: E402
import runner  # noqa: E402
from rhinomcp.server import RhinoConnection  # noqa: E402

FEET_TO_METERS = 0.3048
MM_TO_METERS = 0.001
# Document-unit parameters evaluate_stability accepts; everything else is SI or a ratio.
DOCUMENT_UNIT_PARAMS = ("joint_penetration", "ground_settlement", "stability_threshold", "floor_z")

DEFAULT_CASES = [
    "stair3_step100",
    "stair3_step300",
    "pedestal_eccentric",
    "footing_holds",
    "footing_overturns",
    "cantilever_margin_plus120",
    "cantilever_margin_minus40",
    "bridge_braced",
    "bridge_braced_dynamic",
]

CACHE_KEYS = ("rhinomcp-mod:connectivity-graph-eva", "rhinomcp-mod:connectivity-graph")
# Lists reported in body order, and bodies are numbered in whatever order the graph found
# them - so these are compared as sorted multisets of their numeric fields, not by index.
ORDERED_LISTS = ("joint_forces", "nodes", "ground_sites", "joint_welded_examples")
MULTISET_FIELDS = {
    "joint_forces": ("force_n", "tension_n", "shear_n"),
    "ground_sites": ("fz_n",),
    "nodes": ("diameter_m",),
}
# Below this, a rotation is the solver's noise floor rather than a result.
ROTATION_FLOOR_DEG = 1e-4


def convert_document(connection: RhinoConnection, unit_name: str) -> None:
    code = "\n".join([
        "import Rhino",
        "import scriptcontext",
        "doc = scriptcontext.doc",
        # AdjustModelUnitSystem scales the geometry but leaves ModelAbsoluteTolerance as a
        # bare number, and contact detection works in multiples of that tolerance - so a
        # document that is physically identical must carry a physically identical tolerance.
        "before = Rhino.RhinoMath.UnitScale(doc.ModelUnitSystem, Rhino.UnitSystem.Meters)",
        f"doc.AdjustModelUnitSystem(Rhino.UnitSystem.{unit_name}, True)",
        "after = Rhino.RhinoMath.UnitScale(doc.ModelUnitSystem, Rhino.UnitSystem.Meters)",
        "doc.ModelAbsoluteTolerance = doc.ModelAbsoluteTolerance * before / after",
        *[f'doc.Strings.Delete("{key}")' for key in CACHE_KEYS],
        "doc.Views.Redraw()",
        'print("RHINOMCP_UNIT=" + str(doc.ModelUnitSystem))',
    ])
    output = runner.execute(connection, code)
    if f"RHINOMCP_UNIT={unit_name}" not in output:
        raise runner.CaseFailure(f"document did not convert to {unit_name}: {output}")
    # The graph cache also lives on the objects and in memory; the dashed form runs
    # without prompting, which the undashed one does not (see MCPClearCacheCommand).
    connection.send_command("run_command", {"command": "-mcpmodclearcache"})


def evaluate_scaled(connection: RhinoConnection, case: case_module.Case, ids: list[str],
                    param_scale: float) -> dict[str, Any]:
    """runner.evaluate, with the case's document-unit parameters rescaled."""
    saved = dict(case.params)
    try:
        for key in DOCUMENT_UNIT_PARAMS:
            if key in case.params:
                case.params[key] = saved[key] * param_scale
        return runner.evaluate(connection, case, 1, ids)
    finally:
        case.params.clear()
        case.params.update(saved)


def leaves(value: Any, prefix: str = "") -> dict[str, float]:
    out: dict[str, float] = {}
    if isinstance(value, bool):
        return out
    if isinstance(value, (int, float)):
        out[prefix] = float(value)
    elif isinstance(value, dict):
        for key, item in value.items():
            out.update(leaves(item, f"{prefix}.{key}" if prefix else str(key)))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            out.update(leaves(item, f"{prefix}[{index}]"))
    return out


def classify(path: str, mm: float, ft: float, tolerance: float) -> tuple[str, float]:
    if mm == 0.0 and ft == 0.0:
        return "equal", 0.0
    if mm == 0.0 or ft == 0.0:
        return "DIFFERS", math.inf
    ratio = ft / mm
    scale = FEET_TO_METERS / MM_TO_METERS  # one foot is 304.8 mm
    if abs(ratio - 1.0) <= tolerance:
        return "equal", ratio - 1.0
    if abs(ratio * scale - 1.0) <= tolerance:
        return "document-unit", ratio * scale - 1.0
    if abs(ratio / scale - 1.0) <= tolerance:
        return "document-unit (inverse)", ratio / scale - 1.0
    return "DIFFERS", ratio - 1.0


def compare(name: str, mm: dict[str, Any], ft: dict[str, Any], tolerance: float,
            ignore: tuple[str, ...]) -> list[str]:
    problems: list[str] = []
    if bool(mm.get("stable")) != bool(ft.get("stable")):
        problems.append(f"verdict differs: mm {mm.get('stable')} vs ft {ft.get('stable')}")

    mm_leaves = leaves(mm)
    ft_leaves = leaves(ft)
    doc_unit_fields: list[str] = []
    for path in sorted(set(mm_leaves) | set(ft_leaves)):
        if any(path.startswith(p) or path.endswith(p) for p in ignore):
            continue
        # Per-step traces are diagnostics: two runs that exit at different steps have
        # different lengths, and that says nothing about units. Joint forces are a list in
        # body order, and bodies are numbered in whatever order the graph found them, so
        # they are compared as a sorted multiset below rather than index by index.
        if "_samples" in path or path.split(".")[0].rstrip("0123456789[]") in ORDERED_LISTS:
            continue
        if path not in mm_leaves or path not in ft_leaves:
            problems.append(f"{path}: present in one result only")
            continue
        if path.endswith("_deg") and abs(mm_leaves[path]) < ROTATION_FLOOR_DEG \
                and abs(ft_leaves[path]) < ROTATION_FLOOR_DEG:
            continue
        kind, error = classify(path, mm_leaves[path], ft_leaves[path], tolerance)
        if kind == "DIFFERS":
            problems.append(
                f"{path}: mm {mm_leaves[path]:.6g} vs ft {ft_leaves[path]:.6g} "
                f"(ratio {ft_leaves[path] / mm_leaves[path] if mm_leaves[path] else math.inf:.6g})")
        elif kind.startswith("document-unit"):
            doc_unit_fields.append(path)
    if doc_unit_fields:
        print(f"  document-unit fields (scaled by 304.8 as expected): {', '.join(doc_unit_fields)}")

    for key, fields in MULTISET_FIELDS.items():
        mm_list = mm.get(key)
        ft_list = ft.get(key)
        if not (isinstance(mm_list, list) and isinstance(ft_list, list)):
            continue
        if len(mm_list) != len(ft_list):
            problems.append(f"{key}: {len(mm_list)} entries in mm vs {len(ft_list)} in ft")
            continue
        for field in fields:
            a = sorted(float(r.get(field, 0.0)) for r in mm_list)
            b = sorted(float(r.get(field, 0.0)) for r in ft_list)
            scale = max(abs(x) for x in a + b) or 1.0
            worst = max(abs(x - y) for x, y in zip(a, b)) / scale if a else 0.0
            if worst > tolerance:
                problems.append(
                    f"{key}.{field} (sorted): worst mismatch {worst:.3g} of peak {scale:.6g}")
        print(f"  {key}: {len(mm_list)} entries compared as sorted multisets")
    return problems


def density_check(connection: RhinoConnection, case: case_module.Case) -> list[str]:
    """assign_mass(density=...) in a feet document must reproduce the masses the case stated."""
    ids = runner.build(connection, case)
    # The masses the case stated, read straight off the objects before anything changes.
    output = runner.execute(connection, "\n".join([
        "import scriptcontext, json, System",
        "doc = scriptcontext.doc",
        "out = {}",
        "for obj in doc.Objects:",
        "    raw = obj.Attributes.GetUserString('rhinomcp.stability.v1')",
        "    if raw:",
        "        out[str(obj.Id)] = json.loads(raw)['mass']",
        'print("RHINOMCP_STATED=" + json.dumps(out))',
    ]))
    stated_by_id = {k.lower(): float(v) for k, v in json.loads(
        output.split("RHINOMCP_STATED=", 1)[1].splitlines()[0]).items()}

    convert_document(connection, "Feet")
    assigned = connection.send_command(
        "assign_mass", {"density": case_module.CONCRETE_DENSITY, "ids": ids})
    convert_document(connection, "Millimeters")
    if assigned.get("success") is False:
        return [f"assign_mass failed in feet: {assigned.get('message')}"]

    problems = []
    records = assigned.get("assigned") or []
    if not records:
        # Fall back to whatever the tool returned, so a shape change shows up in the report.
        return [f"assign_mass returned no per-object records: {json.dumps(assigned)[:300]}"]
    for record in records:
        oid = str(record.get("id") or record.get("guid")).lower()
        got = float(record.get("mass", float("nan")))
        want = stated_by_id.get(oid)
        if want is None:
            problems.append(f"{oid}: no stated mass to compare against")
        elif not math.isclose(got, want, rel_tol=1e-6):
            problems.append(f"{oid}: density in feet gave {got:.6g} kg, case stated {want:.6g} kg")
    if not problems:
        print(f"  density in feet reproduced {len(records)} stated masses to 1e-6")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--case", action="append", default=None)
    parser.add_argument("--tolerance", type=float, default=1e-3,
                        help="relative tolerance for SI fields between the two runs")
    parser.add_argument("--integrator", default=None)
    parser.add_argument("--ignore", action="append", default=["seconds", "elapsed", "timing"],
                        help="result paths to leave out of the comparison")
    parser.add_argument("--json", type=pathlib.Path, default=None)
    args = parser.parse_args()

    runner.OVERRIDES["integrator"] = args.integrator
    selected = [case_module.by_name(n) for n in (args.case or DEFAULT_CASES)]

    connection = RhinoConnection(host=runner.HOST, port=runner.PORT)
    if not connection.connect():
        print("could not connect to Rhino on 127.0.0.1:1999", file=sys.stderr)
        return 2
    document = connection.send_command("get_document_info", {"detail": "summary"})
    if document.get("object_count"):
        print("refusing to run in a non-empty document", file=sys.stderr)
        return 2

    report: dict[str, Any] = {}
    failures = 0
    for case in selected:
        label = f"{case.name} [{case.mode}]"
        print(f"== {label}")
        try:
            mm_run = runner.run_once(connection, case, 1)
            mm_result = mm_run["result"]

            ids = runner.build(connection, case)
            runner.apply_rules(connection, case, ids)
            convert_document(connection, "Feet")
            try:
                ft_result = evaluate_scaled(connection, case, ids, MM_TO_METERS / FEET_TO_METERS)
            finally:
                convert_document(connection, "Millimeters")
        except runner.CaseFailure as error:
            print(f"FAIL {label}: {error}")
            report[case.name] = {"error": str(error)}
            failures += 1
            continue

        problems = compare(case.name, mm_result, ft_result, args.tolerance, tuple(args.ignore))
        verdict_mm = "stable" if mm_result.get("stable") else "unstable"
        verdict_ft = "stable" if ft_result.get("stable") else "unstable"
        expected = "stable" if case.stable else "unstable"
        print(f"  mm: {verdict_mm}   ft: {verdict_ft}   expected: {expected}")
        for problem in problems:
            print(f"  DIFF {problem}")
        ok = not problems and verdict_mm == expected
        print("  pass" if ok else "  FAIL")
        failures += 0 if ok else 1
        report[case.name] = {
            "passed": ok, "problems": problems,
            "mm": mm_result, "ft": ft_result,
        }

    # Mass from density, once, on a case whose masses were stated from that same density.
    density_case = case_module.by_name("stair3_step100")
    print("== assign_mass(density) in feet")
    try:
        problems = density_check(connection, density_case)
    except runner.CaseFailure as error:
        problems = [str(error)]
    for problem in problems:
        print(f"  DIFF {problem}")
    print("  pass" if not problems else "  FAIL")
    failures += 0 if not problems else 1
    report["assign_mass_density_feet"] = {"passed": not problems, "problems": problems}

    if args.json:
        args.json.write_text(json.dumps(report, indent=2, default=str))
    print(f"\n{len(selected) + 1 - failures} passed, {failures} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
