"""Settings ordering through the real local HTTP server, without reading user data."""
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import ProxyHandler, Request, build_opener

from dashboard_data import UsageIndex
from dashboard_server import LocalHTTPServer, make_handler
from disk_index import DiskUsageIndex


class RefreshRevisionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='refresh revision ')
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.index = UsageIndex(self.home, refresh_seconds=5)
        self.server = LocalHTTPServer(('127.0.0.1', 0), make_handler(self.index, 'revision-test'))
        self.worker = threading.Thread(target=self.server.serve_forever, kwargs={'poll_interval': 0.02}, daemon=True)
        self.worker.start()
        self.addCleanup(self.close_server)

    def close_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.worker.join(timeout=2)
        self.assertFalse(self.worker.is_alive())

    def post(self, body):
        request = Request(f'http://127.0.0.1:{self.server.server_port}/api/settings',
                          data=json.dumps(body).encode(), headers={'X-Codex-Instance': 'revision-test'})
        with build_opener(ProxyHandler({})).open(request, timeout=3) as response:
            return json.load(response)

    def test_newer_request_wins_over_late_and_duplicate_revisions(self):
        self.assertEqual(self.post({'refresh_seconds': 17, 'refresh_revision': 2}),
                         {'refresh_seconds': 17, 'refresh_revision': 2, 'applied': True})
        for old_revision in (0, 1, 2):
            with self.subTest(revision=old_revision):
                self.assertEqual(self.post({'refresh_seconds': 0, 'refresh_revision': old_revision}),
                                 {'refresh_seconds': 17, 'refresh_revision': 2, 'applied': False})
        self.assertEqual(self.index.refresh_seconds, 17)
        self.assertEqual(self.post({'refresh_seconds': 0, 'refresh_revision': 3})['refresh_seconds'], 0)

    def test_delayed_inflight_old_http_request_cannot_undo_newer_request(self):
        entered, release = threading.Event(), threading.Event()
        configure = self.index.configure_refresh

        def delayed(seconds, revision):
            if revision == 1:
                entered.set()
                if not release.wait(2):
                    raise AssertionError('New settings request failed to finish')
            return configure(seconds, revision)

        with patch.object(self.index, 'configure_refresh', side_effect=delayed), ThreadPoolExecutor(max_workers=1) as pool:
            older = pool.submit(self.post, {'refresh_seconds': 60, 'refresh_revision': 1})
            try:
                self.assertTrue(entered.wait(2))
                newest = self.post({'refresh_seconds': 0, 'refresh_revision': 2})
                self.assertEqual(newest, {'refresh_seconds': 0, 'refresh_revision': 2, 'applied': True})
            finally:
                release.set()
            self.assertEqual(older.result(timeout=3),
                             {'refresh_seconds': 0, 'refresh_revision': 2, 'applied': False})
        self.assertEqual(self.index.refresh_seconds, 0)

    def test_invalid_revision_or_interval_does_not_mutate_either_value(self):
        self.post({'refresh_seconds': 17, 'refresh_revision': 2})
        for invalid in (-1, True, False, None, 1.5, '3', [], {}):
            with self.subTest(revision=invalid):
                with self.assertRaises(HTTPError) as error:
                    self.post({'refresh_seconds': 0, 'refresh_revision': invalid})
                self.assertEqual(error.exception.code, 400)
                error.exception.close()
                self.assertEqual((self.index.refresh_seconds, self.index.refresh_revision), (17, 2))
        with self.assertRaises(HTTPError) as error:
            self.post({'refresh_seconds': 3601, 'refresh_revision': 100})
        self.assertEqual(error.exception.code, 400)
        error.exception.close()
        self.assertEqual((self.index.refresh_seconds, self.index.refresh_revision), (17, 2))
        self.assertTrue(self.post({'refresh_seconds': 9, 'refresh_revision': 3})['applied'])

    def test_unversioned_requests_keep_existing_behavior_without_lowering_watermark(self):
        self.assertEqual(self.post({'refresh_seconds': 9}), {'refresh_seconds': 9})
        self.post({'refresh_seconds': 17, 'refresh_revision': 4})
        self.assertEqual(self.post({'refresh_seconds': 0}), {'refresh_seconds': 0})
        self.assertEqual(self.post({'refresh_seconds': 60, 'refresh_revision': 3}),
                         {'refresh_seconds': 0, 'refresh_revision': 4, 'applied': False})
        self.assertTrue(self.post({'refresh_seconds': 1, 'refresh_revision': 5})['applied'])

    def test_disk_index_restart_resets_revision_without_persisting_it(self):
        cache = self.home / 'usage.sqlite3'
        first = DiskUsageIndex(self.home, cache, refresh_seconds=5)
        first.configure_refresh(17, revision=100)
        self.assertEqual(first.configure_refresh(0, revision=99)['refresh_seconds'], 17)
        restarted = DiskUsageIndex(self.home, cache, refresh_seconds=5)
        self.assertEqual(restarted.configure_refresh(0, revision=0),
                         {'refresh_seconds': 0, 'refresh_revision': 0, 'applied': True})


if __name__ == '__main__':
    unittest.main()
