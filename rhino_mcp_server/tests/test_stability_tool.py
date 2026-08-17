import asyncio
import unittest
from unittest.mock import Mock, patch

from rhinomcp.tools.extended_tools import evaluate_stability


class EvaluateStabilityToolTests(unittest.TestCase):
    def test_default_parameters_match_plugin_defaults(self):
        connection = Mock()
        connection.send_command.return_value = {
            "success": True,
            "evaluation_mode": "single_rigid_assembly",
        }

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            result = asyncio.run(evaluate_stability())

        self.assertTrue(result["success"])
        connection.send_command.assert_called_once_with(
            "evaluate_stability",
            {
                "current_step": 50,
                "stability_threshold": 10.0,
                "rigid_strength": 10000.0,
                "floor_strength": 1000.0,
                "floor_z": 0.0,
                "gravity": 9.81,
                "assign_tol": 1e-6,
                "threshold": 0.001,
                "solver_substeps": 1,
                "display": False,
            },
        )

    def test_graph_objects_are_sent_as_compact_json(self):
        connection = Mock()
        connection.send_command.return_value = {"success": True}

        with patch("rhinomcp.server.get_rhino_connection", return_value=connection):
            asyncio.run(evaluate_stability(graph={"n": [], "e": []}))

        params = connection.send_command.call_args.args[1]
        self.assertEqual('{"n":[],"e":[]}', params["graph"])


if __name__ == "__main__":
    unittest.main()
