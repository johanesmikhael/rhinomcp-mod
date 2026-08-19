import asyncio
import unittest
from unittest.mock import Mock, patch

from rhinomcp.tools.extended_tools import evaluate_stability


class EvaluateStabilityToolTests(unittest.TestCase):
    def test_unit_sensitive_defaults_are_resolved_by_plugin(self):
        connection = Mock()
        connection.send_command.return_value = {
            "success": True,
            "evaluation_mode": "single_rigid_assembly",
        }

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            result = asyncio.run(evaluate_stability())

        self.assertTrue(result["success"])
        # floor_strength is deliberately absent: the plugin sizes it from the
        # assembly's mass so that settling does not exhaust the stability budget.
        connection.send_command.assert_called_once_with(
            "evaluate_stability",
            {
                "current_step": 50,
                "rigid_strength": 10000.0,
                "floor_z": 0.0,
                "gravity": 9.80665,
                "solver_substeps": 1,
                "display": False,
            },
        )

    def test_explicit_floor_strength_overrides_auto_sizing(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            asyncio.run(evaluate_stability(floor_strength=55800.0))

        params = connection.send_command.call_args.args[1]
        self.assertEqual(55800.0, params["floor_strength"])

    def test_explicit_lengths_are_sent_in_document_units(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            asyncio.run(
                evaluate_stability(
                    stability_threshold=0.25,
                    assign_tol=0.0001,
                    threshold=0.002,
                    gravity=9.7,
                )
            )

        params = connection.send_command.call_args.args[1]
        self.assertEqual(0.25, params["stability_threshold"])
        self.assertEqual(0.0001, params["assign_tol"])
        self.assertEqual(0.002, params["threshold"])
        self.assertEqual(9.7, params["gravity"])

    def test_graph_objects_are_sent_as_compact_json(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            asyncio.run(evaluate_stability(graph={"n": [], "e": []}))

        params = connection.send_command.call_args.args[1]
        self.assertEqual('{"n":[],"e":[]}', params["graph"])


if __name__ == "__main__":
    unittest.main()
