import asyncio
import unittest
from unittest.mock import Mock, patch

from rhinomcp.tools.extended_tools import evaluate_stability, get_stability_report


def run(coroutine):
    return asyncio.run(coroutine)


class EvaluateStabilityToolTests(unittest.TestCase):
    def test_defaults_ask_for_a_summary_and_leave_sizing_to_the_plugin(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True, "stable": True}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            result = run(evaluate_stability())

        self.assertTrue(result["success"])
        # Everything unit- or model-dependent is absent: the plugin sizes floor
        # stiffness, joint stiffness and thresholds from the document itself.
        connection.send_command.assert_called_once_with(
            "evaluate_stability",
            {"gravity": 9.80665, "solver_substeps": 1, "display": False, "detail": "summary"},
        )

    def test_full_detail_is_passed_through(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            run(evaluate_stability(detail="full", mode="pinned", joint_type="pin"))

        params = connection.send_command.call_args.args[1]
        self.assertEqual("full", params["detail"])
        self.assertEqual("pinned", params["mode"])
        self.assertEqual("pin", params["joint_type"])

    def test_document_unit_lengths_are_sent_unconverted(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            run(evaluate_stability(stability_threshold=0.25, floor_z=-3.0, joint_penetration=0.01))

        params = connection.send_command.call_args.args[1]
        self.assertEqual(0.25, params["stability_threshold"])
        self.assertEqual(-3.0, params["floor_z"])
        self.assertEqual(0.01, params["joint_penetration"])


class GetStabilityReportToolTests(unittest.TestCase):
    def test_no_section_lists_sections(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True, "sections": {}}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            run(get_stability_report())

        connection.send_command.assert_called_once_with(
            "get_stability_report", {"limit": 20, "offset": 0, "ascending": False}
        )

    def test_filters_and_paging_are_passed_through(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True, "records": []}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            run(
                get_stability_report(
                    section="joint_forces",
                    sort="shear_n",
                    ascending=True,
                    limit=50,
                    offset=100,
                    ids=["abc"],
                    joint_type="pin",
                    min_tension_n=1000.0,
                    reached_capacity_only=True,
                )
            )

        connection.send_command.assert_called_once_with(
            "get_stability_report",
            {
                "limit": 50,
                "offset": 100,
                "ascending": True,
                "section": "joint_forces",
                "sort": "shear_n",
                "ids": ["abc"],
                "joint_type": "pin",
                "min_tension_n": 1000.0,
                "reached_capacity_only": True,
            },
        )


if __name__ == "__main__":
    unittest.main()
