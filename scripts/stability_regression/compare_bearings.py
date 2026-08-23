#!/usr/bin/env python3
"""Sampled bearings against exactly measured ones, on every model the suite draws.

This is the deliverable of step A of CONTACT_PLAN.md, and it exists because three patches
to the sampler were each written without one. The exact measurement is emitted beside the
sampled one and changes nothing; what decides whether it may ever replace it is this table.

    python compare_bearings.py                 # every case that draws geometry
    python compare_bearings.py --case bridge_braced
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "rhino_mcp_server" / "src"))

import cases as case_module
from rhinomcp.server import RhinoConnection

import runner

HOST = "127.0.0.1"
PORT = 1999


def key(a: int, b: int) -> tuple[int, int]:
    return (a, b) if a <= b else (b, a)


def sides(entry: dict) -> tuple[float, float]:
    """Long side first, so two measurements of one rectangle are comparable whichever way
    each of them happened to name its axes."""
    u = float(entry["length_u"])
    v = float(entry["length_v"])
    return (max(u, v), min(u, v))


def angle_between(p: list[float], q: list[float]) -> float:
    dot = sum(x * y for x, y in zip(p, q))
    return math.degrees(math.acos(max(-1.0, min(1.0, abs(dot)))))


def compare(graph: dict) -> list[dict]:
    names = {node["i"]: node.get("name", "") for node in graph.get("n", [])}
    sampled = {key(e["a"], e["b"]): e for e in graph.get("contact_extent") or []}
    exact = {key(e["a"], e["b"]): e for e in graph.get("contact_extent_exact") or []}

    rows = []
    for pair in sorted(set(sampled) | set(exact)):
        s = sampled.get(pair)
        x = exact.get(pair)
        row = {
            "joint": f"{names.get(pair[0], pair[0])}-{names.get(pair[1], pair[1])}",
            "sampled": sides(s) if s else None,
            "exact": sides(x) if x else None,
            "penetration": x["penetration_depth"] if x else None,
            "pieces": x["pieces"] if x else None,
        }
        if s and x:
            sl, ss = row["sampled"]
            xl, xs = row["exact"]
            row["long_pct"] = 100.0 * (sl - xl) / xl if xl else None
            row["short_pct"] = 100.0 * (ss - xs) / xs if xs else None
            row["normal_deg"] = angle_between(s["normal"], x["normal"])
            row["centre_mm"] = max(abs(a - b) for a, b in zip(s["centre"], x["centre"]))
        rows.append(row)
    return rows


def fmt(row: dict) -> str:
    def pair(value):
        return "       -  x       -" if value is None else f"{value[0]:8.1f} x {value[1]:7.1f}"

    def pct(value):
        return "     -" if value is None else f"{value:+6.1f}"

    def deg(value):
        return "    -" if value is None else f"{value:5.1f}"

    penetration = row.get("penetration")
    return (
        f"  {row['joint']:<28} sampled {pair(row['sampled'])}"
        f"   exact {pair(row['exact'])}"
        f"   d% {pct(row.get('long_pct'))}/{pct(row.get('short_pct'))}"
        f"   normal {deg(row.get('normal_deg'))}"
        f"   pen {'-' if penetration is None else format(penetration, '.1f')}"
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--case", action="append", default=None)
    parser.add_argument("--json", default=None)
    args = parser.parse_args()

    wanted = case_module.CASES
    if args.case:
        wanted = [case_module.by_name(name) for name in args.case]

    # One model per distinct build, because several cases draw the same geometry and only
    # differ in what they ask the solver.
    seen: set[str] = set()
    connection = RhinoConnection(host=HOST, port=PORT)
    report: dict[str, list[dict]] = {}

    for case in wanted:
        body = case.build() if callable(case.build) else case.build
        if body in seen:
            continue
        seen.add(body)

        try:
            ids = runner.build(connection, case)
        except Exception as error:  # noqa: BLE001 - a build failure is a row, not a stop
            print(f"{case.name}: BUILD FAILED {error}")
            continue

        # Scoping by id marks the cached graph dirty, which is the only way to be sure the
        # numbers below were measured rather than restored. A cached read reports no
        # samples at all and looks exactly like a sampler that never ran.
        graph = connection.send_command("get_connectivity_graph", {"ids": ids})
        rows = compare(graph)
        report[case.name] = rows

        matched = sum(1 for r in rows if r["sampled"] and r["exact"])
        only_sampled = sum(1 for r in rows if r["sampled"] and not r["exact"])
        only_exact = sum(1 for r in rows if r["exact"] and not r["sampled"])
        print(
            f"{case.name}  ({graph.get('source')})  joints {len(rows)}"
            f"  both {matched}  sampled-only {only_sampled}  exact-only {only_exact}")
        for row in rows:
            print(fmt(row))

    if args.json:
        pathlib.Path(args.json).write_text(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
