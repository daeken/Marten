"""Run with python3 -m unittest discover -s tests -p 'test_marten_ext.py'."""
import contextlib
import http.client
import io
import runpy
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.error import URLError

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "marten-ext"


class InboxDisconnectTests(unittest.TestCase):
    def run_inbox(self, opener):
        stderr = io.StringIO()
        with patch.dict("os.environ", {"MARTEN_TOKEN": "test"}), patch(
            "sys.argv", [str(SCRIPT), "inbox", "--wait", "120"]
        ), contextlib.redirect_stderr(stderr), opener as urlopen:
            with self.assertRaises(SystemExit) as result:
                runpy.run_path(str(SCRIPT), run_name="__main__")
            self.assertEqual(result.exception.code, 2)
            urlopen.assert_called_once()  # Do not replay an acknowledgement.
        self.assertEqual(stderr.getvalue(), "GET /ext/inbox: connection to Marten failed. Is Marten running?\n")

    def test_connection_errors_while_waiting_for_headers(self):
        for error in [ConnectionResetError(), http.client.RemoteDisconnected(), URLError("refused")]:
            with self.subTest(error=type(error)):
                self.run_inbox(patch("urllib.request.urlopen", side_effect=error))

    def test_incomplete_body(self):
        opener = patch("urllib.request.urlopen")
        with opener as urlopen:
            response = urlopen.return_value.__enter__.return_value
            response.read.side_effect = http.client.IncompleteRead(b"[")
            # Use the configured mock for the scope of the call.
            self.run_inbox(patch("urllib.request.urlopen", urlopen))


if __name__ == "__main__":
    unittest.main()
