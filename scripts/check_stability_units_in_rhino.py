#!/usr/bin/env python3
"""Compare equivalent metric and imperial stability runs in a blank Rhino document."""

from __future__ import annotations

import base64
import json
import math

from rhinomcp.server import RhinoConnection


HOST = "127.0.0.1"
PORT = 1999
EVALUATION_GRAPH_KEY = "rhinomcp-mod:connectivity-graph-eva"


def execute_code(connection: RhinoConnection, code: str) -> str:
    result = connection.send_command(
        "execute_rhinoscript_python_code",
        {"code": code},
    )
    if result.get("success") is not True:
        raise RuntimeError(result.get("message", "Rhino Python execution failed"))
    return result.get("result", "")


def marked_value(output: str, marker: str) -> str:
    start = output.find(marker)
    if start < 0:
        raise RuntimeError(f"Rhino output did not contain {marker!r}: {output}")
    return output[start + len(marker) :].splitlines()[0].strip()


def prepare_box(
    connection: RhinoConnection,
    unit_name: str,
    length_to_meters: float,
    mass_unit: str | None = "kg",
) -> str:
    half_width = 0.5 / length_to_meters
    bottom = 0.25 / length_to_meters
    top = 1.25 / length_to_meters
    code = f"""
import Rhino
import scriptcontext

doc = scriptcontext.doc
doc.AdjustModelUnitSystem(Rhino.UnitSystem.{unit_name}, False)
bbox = Rhino.Geometry.BoundingBox(
    Rhino.Geometry.Point3d({-half_width!r}, {-half_width!r}, {bottom!r}),
    Rhino.Geometry.Point3d({half_width!r}, {half_width!r}, {top!r}))
object_id = doc.Objects.AddBrep(bbox.ToBrep())
obj = doc.Objects.FindId(object_id)
obj.Attributes.SetUserString(
    "rhinomcp.stability.v1",
    {json.dumps(json.dumps({"mass": 10.0, **({"mass_unit": mass_unit} if mass_unit else {})}))})
obj.CommitChanges()
doc.Views.Redraw()
print("RHINOMCP_TEST_GUID=" + str(object_id))
"""
    return marked_value(execute_code(connection, code), "RHINOMCP_TEST_GUID=")


def evaluate(
    connection: RhinoConnection,
    object_id: str,
    mass_unit: str | None = "kg",
) -> tuple[dict, dict]:
    node = {"g": object_id, "mass": 10.0}
    if mass_unit:
        node["mass_unit"] = mass_unit
    graph = {"n": [node], "e": []}
    result = connection.send_command(
        "evaluate_stability",
        {"graph": json.dumps(graph, separators=(",", ":")), "display": False},
    )
    if result.get("success") is not True:
        raise RuntimeError(result.get("message", "Stability evaluation failed"))

    output = execute_code(
        connection,
        "import System\n"
        "import scriptcontext\n"
        f'value = scriptcontext.doc.Strings.GetValue("{EVALUATION_GRAPH_KEY}")\n'
        "encoded = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))\n"
        'print("RHINOMCP_TEST_GRAPH=" + encoded)',
    )
    encoded_graph = marked_value(output, "RHINOMCP_TEST_GRAPH=")
    evaluation_graph = json.loads(base64.b64decode(encoded_graph).decode("utf-8"))
    return result, evaluation_graph


def assert_legacy_mass_inference(connection: RhinoConnection) -> None:
    for unit_name, scale, expected_mass_kg in (
        ("Millimeters", 0.001, 10.0),
        ("Feet", 0.3048, 10.0 * 0.45359237),
    ):
        object_id = prepare_box(connection, unit_name, scale, mass_unit=None)
        try:
            result, graph = evaluate(connection, object_id, mass_unit=None)
            if not result.get("unit_warnings"):
                raise AssertionError(f"Legacy {unit_name} mass did not produce a warning")
            assert_close(
                f"legacy {unit_name} mass",
                graph["n"][0]["mass"],
                expected_mass_kg,
                1e-12,
            )
            if graph["n"][0]["mass_unit"] != "kg":
                raise AssertionError("Evaluated legacy mass was not tagged as canonical kg")
        finally:
            clear_case(connection, object_id)


def assert_malformed_mass_unit_is_rejected(connection: RhinoConnection) -> None:
    object_id = prepare_box(connection, "Meters", 1.0, mass_unit="lbf")
    try:
        graph = {"n": [{"g": object_id, "mass": 10.0, "mass_unit": "lbf"}], "e": []}
        result = connection.send_command(
            "evaluate_stability",
            {"graph": json.dumps(graph, separators=(",", ":")), "display": False},
        )
        if result.get("success") is not False or "unsupported mass_unit" not in result.get("message", ""):
            raise AssertionError(f"Malformed mass unit was not rejected: {result}")
    finally:
        clear_case(connection, object_id)


def assert_unsupported_document_unit_is_rejected(connection: RhinoConnection) -> None:
    execute_code(
        connection,
        "import Rhino\n"
        "import scriptcontext\n"
        "scriptcontext.doc.AdjustModelUnitSystem(Rhino.UnitSystem.None, False)",
    )
    try:
        result = connection.send_command(
            "evaluate_stability",
            {"graph": '{"n":[],"e":[]}', "display": False},
        )
        message = result.get("message", "")
        if result.get("success") is not False or "cannot be normalized to meters" not in message:
            raise AssertionError(f"Unsupported document unit was not rejected: {result}")
    finally:
        execute_code(
            connection,
            "import Rhino\n"
            "import scriptcontext\n"
            "scriptcontext.doc.AdjustModelUnitSystem(Rhino.UnitSystem.Inches, False)",
        )


def clear_case(connection: RhinoConnection, object_id: str) -> None:
    connection.send_command(
        "delete_objects",
        {"ids": [object_id], "confirm": True},
    )
    code = """
import scriptcontext

doc = scriptcontext.doc
doc.Strings.Delete("rhinomcp-mod:connectivity-graph-eva")
doc.Strings.Delete("rhinomcp-mod:connectivity-graph")
doc.Views.Redraw()
"""
    execute_code(connection, code)


def assert_close(label: str, left: float, right: float, tolerance: float) -> None:
    if not math.isclose(left, right, rel_tol=tolerance, abs_tol=tolerance):
        raise AssertionError(f"{label}: {left!r} != {right!r}")


def main() -> None:
    connection = RhinoConnection(HOST, PORT)
    document = connection.send_command("get_document_info", {"detail": "summary"})
    if document.get("object_count") != 0:
        raise RuntimeError("Acceptance test refuses to modify a non-empty Rhino document")

    cases = []
    for label, unit_name, scale in (
        ("millimeters", "Millimeters", 0.001),
        ("inches", "Inches", 0.0254),
    ):
        inventory = connection.send_command("get_document_info", {"detail": "summary"})
        if inventory.get("object_count") != 0:
            raise RuntimeError("Acceptance test refuses to modify a non-empty Rhino document")
        object_id = prepare_box(connection, unit_name, scale)
        try:
            result, graph = evaluate(connection, object_id)
            cases.append((label, scale, result, graph))
        finally:
            clear_case(connection, object_id)

    metric_label, metric_scale, metric_result, metric_graph = cases[0]
    imperial_label, imperial_scale, imperial_result, imperial_graph = cases[1]

    if metric_result["stable"] != imperial_result["stable"]:
        raise AssertionError("Metric and imperial stability classifications differ")
    assert_close("metric scale", metric_scale, metric_result["length_to_meters"], 1e-12)
    assert_close("imperial scale", imperial_scale, imperial_result["length_to_meters"], 1e-12)
    assert_close("gravity", metric_result["gravity_m_s2"], imperial_result["gravity_m_s2"], 1e-12)
    assert_close(
        "normalized displacement",
        metric_result["max_displacement_m"],
        imperial_result["max_displacement_m"],
        1e-6,
    )

    metric_transform = metric_graph["n"][0]["transform"]
    imperial_transform = imperial_graph["n"][0]["transform"]
    for row in range(3):
        for column in range(3):
            assert_close(
                f"rotation[{row},{column}]",
                metric_transform[row][column],
                imperial_transform[row][column],
                1e-6,
            )
        assert_close(
            f"translation_m[{row}]",
            metric_transform[row][3] * metric_scale,
            imperial_transform[row][3] * imperial_scale,
            1e-6,
        )

    assert_legacy_mass_inference(connection)
    assert_malformed_mass_unit_is_rejected(connection)
    assert_unsupported_document_unit_is_rejected(connection)

    print(
        json.dumps(
            {
                metric_label: metric_result,
                imperial_label: imperial_result,
                "classification_matches": True,
                "normalized_transform_matches": True,
                "legacy_mass_inference_matches": True,
                "malformed_mass_unit_rejected": True,
                "unsupported_document_unit_rejected": True,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
