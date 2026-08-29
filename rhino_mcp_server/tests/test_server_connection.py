import json
import unittest

from rhinomcp.server import RhinoConnection


class FakeSocket:
    def __init__(self, responses):
        self.responses = list(responses)
        self.sent = []
        self.timeouts = []
        self.closed = False

    def sendall(self, payload):
        self.sent.append(payload)

    def recv(self, _buffer_size):
        return self.responses.pop(0) if self.responses else b""

    def settimeout(self, timeout):
        self.timeouts.append(timeout)

    def close(self):
        self.closed = True


class RhinoConnectionTests(unittest.TestCase):
    def test_returns_successful_result(self):
        response = json.dumps(
            {"status": "success", "result": {"value": 42}}
        ).encode("utf-8")
        sock = FakeSocket([response])
        connection = RhinoConnection("127.0.0.1", 1999, sock=sock)

        result = connection.send_command("get_document_info", {"detail": "summary"})

        self.assertEqual({"value": 42}, result)
        self.assertEqual(1, len(sock.sent))
        self.assertFalse(sock.closed)
        self.assertEqual(15.0, sock.timeouts[-1])

    def test_never_replays_command_after_empty_response(self):
        sock = FakeSocket([b""])
        connection = RhinoConnection("127.0.0.1", 1999, sock=sock)

        with self.assertRaisesRegex(Exception, "Connection closed"):
            connection.send_command("create_object", {"type": "POINT"})

        self.assertEqual(1, len(sock.sent))
        self.assertTrue(sock.closed)
        self.assertIsNone(connection.sock)


if __name__ == "__main__":
    unittest.main()
