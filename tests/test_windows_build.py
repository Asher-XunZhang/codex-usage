"""Cross-platform release boundary checks and native Windows process ownership."""
import copy
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / 'backend'))
from scripts.fetch_windows_runtime import extract_runtime, load_manifest, PTH
from scripts.package_windows import check_path, validate_app
from scripts.build_windows import SDK_VERSION, version
from dashboard_server import LocalHTTPServer, make_handler
from dashboard_data import UsageIndex


class WindowsDistributionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)

    def test_manifest_is_pinned_and_rejects_download_override(self):
        expected = load_manifest()
        self.assertEqual(expected['version'], '3.14.7')
        for name, value in [('url', 'https://example.invalid/python.zip'), ('sha256', '0' * 64),
                            ('version', '3.14.6'), ('file', '../python.zip'), ('bytes', True)]:
            with self.subTest(name=name):
                changed = copy.deepcopy(expected)
                changed[name] = value
                path = self.root / 'manifest.json'
                path.write_text(json.dumps(changed), encoding='utf-8')
                with self.assertRaises(ValueError):
                    load_manifest(path)

    def test_release_path_rejects_windows_traversal_private_data_and_aliases(self):
        for name in ('../outside', 'C:/outside', 'backend\\file.py', 'Auth.JSON', 'source/a.JSONL',
                     'python/library.dll:stream', 'logs/a.log', 'source/windows/obj/a.cs',
                     'NUL.txt', 'source/CON', 'readme.', 'name /child', '.local/data'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                check_path(name)
        for name in ('CodexUsage.exe', 'backend/disk_index.py', 'source/windows/App.cs', 'python/python314.zip'):
            check_path(name)

    def test_runtime_archive_is_checked_before_extraction(self):
        bad = self.root / 'bad.zip'
        bad.write_bytes(b'not the pinned archive')
        output = self.root / 'python'
        with self.assertRaises(ValueError):
            extract_runtime(bad, output, load_manifest())
        self.assertFalse(output.exists())

    def test_runtime_extraction_refuses_nested_paths_and_preserves_isolation(self):
        runtime = self.root / 'runtime.zip'
        for member in ('../escape', 'sub/file.dll', 'python.exe:stream', 'file\\evil'):
            with zipfile.ZipFile(runtime, 'w') as archive:
                archive.writestr(member, b'bad')
            with patch('scripts.fetch_windows_runtime.verify_runtime'), self.assertRaises(ValueError):
                extract_runtime(runtime, self.root / str(len(list(self.root.iterdir()))), load_manifest())
        with zipfile.ZipFile(runtime, 'w') as archive:
            for name in ('python.exe', 'pythonw.exe', 'python314.dll', 'python314.zip', 'python314._pth',
                         'LICENSE.txt', '_sqlite3.pyd', 'sqlite3.dll'):
                archive.writestr(name, b'fixture')
        with patch('scripts.fetch_windows_runtime.verify_runtime'):
            extract_runtime(runtime, self.root / 'valid', load_manifest())
        self.assertEqual((self.root / 'valid/python314._pth').read_text(encoding='utf-8'), PTH)
        self.assertNotIn('import site\n', PTH)
        self.assertFalse((self.root.parent / 'escape').exists())

    def test_build_inventory_rejects_unmanifested_local_data(self):
        (self.root / 'BUILD-MANIFEST.json').write_text(json.dumps(dict(
            architecture='win-x64', python=load_manifest(), version=version(), dotnet_sdk=SDK_VERSION, files={})), encoding='utf-8')
        (self.root / 'private.jsonl').write_text('private', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'inventory differs'):
            validate_app(self.root, check_sources=False)


@unittest.skipUnless(os.name == 'nt', 'Requires Windows process and socket APIs')
class WindowsOwnershipTests(unittest.TestCase):
    def test_job_close_terminates_only_the_owned_child(self):
        from windows_job import WindowsChildJob
        child = subprocess.Popen([sys.executable, '-E', '-s', '-B', '-c', 'import time; time.sleep(60)'],
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        job = WindowsChildJob()
        try:
            job.assign(child.pid)
            self.assertIsNone(child.poll())
            job.close()
            self.assertIsNotNone(child.wait(timeout=5))
        finally:
            job.close()
            if child.poll() is None:
                child.kill()
            child.wait(timeout=5)

    def test_local_http_port_cannot_be_taken_over_with_reuseaddr(self):
        with tempfile.TemporaryDirectory() as directory:
            index = UsageIndex(Path(directory), refresh_seconds=0)
            with LocalHTTPServer(('127.0.0.1', 0), make_handler(index, 'exclusive-test')) as server:
                with socket.socket() as competing:
                    competing.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    with self.assertRaises(OSError):
                        competing.bind(server.server_address)


if __name__ == '__main__':
    unittest.main()
