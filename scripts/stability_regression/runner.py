#!/usr/bin/env python3
"""Run the stability regression suite against a live Rhino.

Three tiers, for three different questions.

The micro tier asks whether a *number* is right rather than whether a verdict is. Its cases
have closed-form answers - a load over three columns of known EA/L shortens them by W/3k,
a body with nothing under it falls at g - so it can say an integrator is wrong instead of
merely disagreeing with the other one. Every one of its cases is run against both
integrators, because that is the comparison it exists to settle.

The fast tier runs every case at the shipped default budget and asks only whether the
verdict is right. It is cheap enough to run on every edit.

The slow tier sweeps the solver budget upward and records the smallest one at which the
verdict becomes right. That number is the point: relaxation converges toward equilibrium
rather than falling, so a mechanism creeps instead of collapsing and the verdict currently
depends on how long it is allowed to creep. Recording the ceiling turns that dependence
into a measurement. A run is a pass when no ceiling has risen above its baseline.

    python runner.py                 # fast tier
    python runner.py --tier slow     # budget sweep, writes nothing
    python runner.py --tier slow --update-baseline
    python runner.py --case bridge_braced --tier all
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
import time
from typing import Any

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "rhino_mcp_server" / "src"))

import cases as case_module
from cases import FAST, GEOMETRY, MICRO, SLOW, SYSTEMS, Case
from rhinomcp.server import RhinoConnection


HOST = "127.0.0.1"
PORT = 1999

BASELINE = pathlib.Path(__file__).resolve().parent / "baseline.json"

# Budgets the slow tier tries, in order. 1 is the shipped default; the run stops at the
# first that gives the right answer, so a case that is already correct costs one run.
SWEEP = [1, 3, 5, 8, 12, 15, 20]


class CaseFailure(Exception):
    pass


# Command-line overrides, applied only where a case has not spoken for itself.
OVERRIDES: dict[str, Any] = {}


def execute(connection: RhinoConnection, code: str) -> str:
    result = connection.send_command("execute_rhinoscript_python_code", {"code": code})
    if result.get("success") is not True:
        raise CaseFailure(result.get("message", "Rhino Python execution failed"))
    return result.get("result", "")


def build(connection: RhinoConnection, case: Case) -> list[str]:
    """Draw the case and return the GUIDs it added.

    Scoping the evaluation by GUID rather than by layer is not a style choice: a layer that
    already exists makes LayerTable.Add return -1, every object silently lands on layer 0,
    and the layer-scoped run then matches nothing.
    """
    body = case.build() if callable(case.build) else case.build
    output = execute(connection, case_module.script(body))
    marker = "RHINOMCP_BUILT="
    if marker not in output:
        raise CaseFailure(f"build produced no object list: {output}")
    ids = json.loads(output.split(marker, 1)[1].splitlines()[0].strip())
    if not ids:
        raise CaseFailure("build added no objects")
    return ids


def apply_rules(connection: RhinoConnection, case: Case, ids: list[str]) -> None:
    """The case's joint-type rules, with "*" standing for everything it built."""
    for rule in case.rules:
        payload = dict(rule)
        if payload.get("ids") == "*":
            payload["ids"] = ids
        if payload.get("with_ids") == "*":
            payload["with_ids"] = ids
        connection.send_command("assign_joint_type", payload)


def evaluate(
    connection: RhinoConnection, case: Case, substeps: int, ids: list[str]
) -> dict[str, Any]:
    params: dict[str, Any] = {
        "mode": case.mode,
        "solver_substeps": substeps,
        "ids": ids,
        "gravity": 9.80665,
        "display": False,
    }
    params.update(case.params)

    # Applied under the case's own parameters, never over them.
    if OVERRIDES.get("integrator") and case.mode in ("pinned", "pinned_dynamic"):
        params.setdefault("integrator", OVERRIDES["integrator"])
        if OVERRIDES["integrator"] == "rigid_bodies":
            # The rigid path damps joints against relative motion, which barely touches a
            # mode where both ends move together, so its 2% is not the particle path's 2%.
            params.setdefault("damping_ratio", 0.2)
    if OVERRIDES.get("fast") and case.mode in ("pinned", "pinned_dynamic"):
        params.setdefault("lateral_load_fraction", 0.0)
        params.setdefault("timestep_safety", 0.4)
    if OVERRIDES.get("timestep_safety") and case.mode in ("pinned", "pinned_dynamic"):
        params["timestep_safety"] = OVERRIDES["timestep_safety"]

    result = connection.send_command("evaluate_stability", params)
    if result.get("success") is not True:
        raise CaseFailure(result.get("message") or json.dumps(result)[:400])
    return result


def check_numbers(case: Case, result: dict[str, Any]) -> list[str]:
    problems = []
    for key, (low, high) in case.expect.items():
        # Dotted keys reach into nested reports, so a case can assert on a measured
        # stiffness without the result having to be flattened.
        value = result
        for part in key.split("."):
            value = value.get(part) if isinstance(value, dict) else None
        if value is None:
            problems.append(f"{key} missing from result")
        elif not (low <= float(value) <= high):
            problems.append(f"{key} = {float(value):.6g}, expected {low:.6g}..{high:.6g}")
    return problems


def run_once(connection: RhinoConnection, case: Case, substeps: int) -> dict[str, Any]:
    started = time.monotonic()
    ids = build(connection, case)

    # Cases that check something upstream of the solver never run it. The bearing extent is
    # decided while the graph is built, and evaluating stability on top would only add a
    # second thing that could fail.
    if case.check is not None:
        def send(command: str, params: dict[str, Any]) -> Any:
            payload = dict(params)
            return connection.send_command(command, payload)

        problems = case.check(send, ids)
        return {
            "objects": len(ids),
            "stable": not problems,
            "correct": not problems,
            "iterations": None,
            "seconds": time.monotonic() - started,
            "problems": problems,
            "result": {},
        }

    apply_rules(connection, case, ids)
    result = evaluate(connection, case, substeps, ids)
    return {
        "objects": len(ids),
        "stable": bool(result.get("stable")),
        "correct": bool(result.get("stable")) == case.stable,
        # pinned_dynamic reports timesteps rather than solver iterations.
        "iterations": result.get("solver_steps_run") or result.get("steps_run"),
        "seconds": time.monotonic() - started,
        "problems": check_numbers(case, result),
        "result": result,
    }


def run_fast(connection: RhinoConnection, case: Case) -> dict[str, Any]:
    run = run_once(connection, case, 1)
    run["passed"] = run["correct"] and not run["problems"]
    return run


def run_sweep(connection: RhinoConnection, case: Case, ceiling: int | None) -> dict[str, Any]:
    attempts = []
    for substeps in SWEEP:
        run = run_once(connection, case, substeps)
        attempts.append({
            "substeps": substeps,
            "stable": run["stable"],
            "correct": run["correct"],
            "iterations": run["iterations"],
            "seconds": round(run["seconds"], 1),
        })
        if run["correct"] and not run["problems"]:
            # A rise above the recorded ceiling is the regression this tier exists to
            # catch: the same physics needing more iterations to reach the same answer.
            passed = ceiling is None or substeps <= ceiling
            return {
                "reached": substeps,
                "attempts": attempts,
                "passed": passed,
                "problems": run["problems"],
                "result": run["result"],
            }
        last_problems = run["problems"]

    return {
        "reached": None,
        "attempts": attempts,
        "passed": False,
        "problems": last_problems,
        "result": run["result"],
    }


def load_baseline() -> dict[str, Any]:
    if BASELINE.exists():
        return json.loads(BASELINE.read_text())
    return {"ceilings": {}}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--tier", default=FAST, choices=[FAST, GEOMETRY, MICRO, SLOW, SYSTEMS, "all"])
    parser.add_argument("--case", action="append", default=None)
    parser.add_argument("--update-baseline", action="store_true")
    # Run every pinned case on one integrator, to compare the two across the whole suite
    # rather than on whichever model is at hand. Cases that name an integrator themselves
    # keep it - the free-fall pair exists precisely to test one each.
    parser.add_argument("--integrator", default=None)
    parser.add_argument("--fast-settings", action="store_true",
                        help="skip the sway probe and take the coarser step, for screening")
    parser.add_argument("--timestep-safety", type=float, default=None,
                        help="rigid-body path only; the particle path has no such knob")
    parser.add_argument("--show", action="store_true",
                        help="draw the graph and leave the settled geometry on screen")
    parser.add_argument("--json", type=pathlib.Path, default=None)
    args = parser.parse_args()

    if args.case:
        selected = [case_module.by_name(name) for name in args.case]
    elif args.tier == "all":
        selected = list(case_module.CASES)
    else:
        selected = case_module.in_tier(args.tier)

    case_module.SHOW_WORK = args.show
    OVERRIDES["integrator"] = args.integrator
    OVERRIDES["fast"] = args.fast_settings
    OVERRIDES["timestep_safety"] = args.timestep_safety

    baseline = load_baseline()
    ceilings = dict(baseline.get("ceilings", {}))

    connection = RhinoConnection(host=HOST, port=PORT)
    if not connection.connect():
        print("could not connect to Rhino on 127.0.0.1:1999", file=sys.stderr)
        return 2

    report: dict[str, Any] = {"cases": {}}
    failures = 0

    for case in selected:
        sweeping = case.tier == SLOW or args.tier == SLOW
        label = f"{case.name} [{case.mode}]"
        try:
            if sweeping:
                run = run_sweep(connection, case, ceilings.get(case.name))
            else:
                run = run_fast(connection, case)
        except CaseFailure as error:
            print(f"FAIL {label}: {error}")
            report["cases"][case.name] = {"error": str(error)}
            failures += 1
            continue

        report["cases"][case.name] = {
            key: value for key, value in run.items() if key != "result"}

        if sweeping:
            reached = run["reached"]
            was = ceilings.get(case.name)
            detail = f"budget {reached}" if reached else "never correct"
            if reached is not None:
                if args.update_baseline:
                    ceilings[case.name] = reached
                elif was is not None and reached > was:
                    detail += f" (baseline {was} - RISEN)"
                elif was is not None:
                    detail += f" (baseline {was})"
        else:
            detail = f"{'stable' if run['stable'] else 'unstable'}, {run['iterations']} iters"

        if run["passed"]:
            print(f"pass {label}: {detail}")
        else:
            failures += 1
            print(f"FAIL {label}: {detail}")
            print(f"     expected {'stable' if case.stable else 'unstable'} - {case.reason}")
            for problem in run.get("problems", []):
                print(f"     {problem}")

    if args.update_baseline:
        BASELINE.write_text(json.dumps({"ceilings": ceilings}, indent=2, sort_keys=True) + "\n")
        print(f"baseline written to {BASELINE}")

    if args.json:
        args.json.write_text(json.dumps(report, indent=2, sort_keys=True, default=str) + "\n")

    print(f"\n{len(selected) - failures}/{len(selected)} passed")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
