"""Cross-platform release boundary checks and native Windows process ownership."""
from tools.common.paths import ROOT, BACKEND, macos_source
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(BACKEND))
from tools.windows.fetch_runtime import extract_runtime, load_manifest, PTH
from tools.windows.package import check_path, validate_app
from tools.windows.build import SDK_VERSION, SDK_NOTICES, version, dependency_notices, dependency_notice_text
import tools.windows.package as windows_package
import tools.windows.verify_release as windows_verify
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
            with patch('tools.windows.fetch_runtime.verify_runtime'), self.assertRaises(ValueError):
                extract_runtime(runtime, self.root / str(len(list(self.root.iterdir()))), load_manifest())
        with zipfile.ZipFile(runtime, 'w') as archive:
            for name in ('python.exe', 'pythonw.exe', 'python314.dll', 'python314.zip', 'python314._pth',
                         'LICENSE.txt', '_sqlite3.pyd', 'sqlite3.dll'):
                archive.writestr(name, b'fixture')
        with patch('tools.windows.fetch_runtime.verify_runtime'):
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

    def notification_fixture(self):
        stage = self.root / 'publish'
        stage.mkdir()
        cache = self.root / 'nuget'
        target, libraries, restored = {}, {}, {}
        for package, release, dlls, runtimepack in (
                ('Microsoft.Toolkit.Uwp.Notifications', '7.1.3', ['Microsoft.Toolkit.Uwp.Notifications.dll'], False),
                ('Microsoft.Windows.SDK.NET.Ref', '10.0.19041.57', ['Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll'], True)):
            directory = cache / package.lower() / release
            directory.mkdir(parents=True)
            license_decl = '<license type="expression">MIT</license>' if not runtimepack else ''
            (directory / (package.lower() + '.nuspec')).write_text(
                '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>'
                f'<id>{package}</id><version>{release}</version>{license_decl}'
                '<licenseUrl>https://example.invalid/license</licenseUrl></metadata></package>', encoding='utf-8')
            if not runtimepack:
                (directory / 'License.md').write_text('Exact publisher license and copyright', encoding='utf-8')
            key = ('runtimepack.' if runtimepack else '') + package + '/' + release
            target[key] = {'runtime': {dll: {} for dll in dlls}}
            libraries[key] = {'type': 'runtimepack' if runtimepack else 'package'}
            if not runtimepack:
                restored[key] = {'type': 'package'}
            for dll in dlls:
                (stage / dll).write_bytes(b'fixture assembly')
        # Ordinary framework runtime packs keep using the already bundled .NET notices.
        libraries['runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12'] = {'type': 'runtimepack'}
        (stage / 'CodexUsage.deps.json').write_text(json.dumps({
            'runtimeTarget': {'name': 'target'}, 'targets': {'target': target}, 'libraries': libraries}), encoding='utf-8')
        assets = self.root / 'project.assets.json'
        assets.write_text(json.dumps({'libraries': restored, 'project': {'frameworks': {'target': {
            'downloadDependencies': [{'name': 'Microsoft.Windows.SDK.NET.Ref', 'version': '[10.0.19041.57, 10.0.19041.57]'}]}}}}), encoding='utf-8')
        return stage, assets, cache

    def test_published_notification_dlls_include_exact_package_and_sdk_licenses(self):
        stage, assets, cache = self.notification_fixture()
        reports = dependency_notices(stage, assets, cache)
        self.assertEqual({name for item in reports for name in item['binaries']}, {
            'Microsoft.Toolkit.Uwp.Notifications.dll', 'Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll'})
        self.assertEqual((stage / 'licenses/Microsoft.Toolkit.Uwp.Notifications-7.1.3-License.md').read_text(encoding='utf-8'),
                         'Exact publisher license and copyright')
        for name, _ in SDK_NOTICES:
            self.assertEqual((stage / 'licenses' / name).read_bytes(), (ROOT / 'resources/windows/licenses' / name).read_bytes())
        text = dependency_notice_text(reports)
        self.assertIn('Microsoft.Toolkit.Uwp.Notifications 7.1.3', text)
        self.assertIn('Microsoft.Windows.SDK.NET.Ref 10.0.19041.57', text)
        self.assertIn('WinRT.Runtime.dll', text)
        self.assertTrue(all((stage / 'licenses' / item['metadata']).is_file() for item in reports))

    def test_dependency_distribution_fails_when_license_is_missing(self):
        stage, assets, cache = self.notification_fixture()
        (cache / 'microsoft.toolkit.uwp.notifications/7.1.3/License.md').unlink()
        with self.assertRaisesRegex(ValueError, 'no bundled license'):
            dependency_notices(stage, assets, cache)

    def test_sdk_runtime_pack_must_match_restored_download_dependency(self):
        stage, assets, cache = self.notification_fixture()
        data = json.loads(assets.read_text(encoding='utf-8'))
        data['project']['frameworks']['target']['downloadDependencies'][0]['version'] = '[10.0.1, 10.0.1]'
        assets.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'does not match restored assets'):
            dependency_notices(stage, assets, cache)

    def test_changed_pinned_sdk_license_is_rejected_without_downloading(self):
        stage, assets, cache = self.notification_fixture()
        with self.assertRaisesRegex(ValueError, 'pinned SDK distribution license'):
            dependency_notices(stage, assets, cache, root=self.root)

    def test_declared_but_missing_runtime_binary_cannot_be_packaged(self):
        stage, assets, cache = self.notification_fixture()
        (stage / 'WinRT.Runtime.dll').unlink()
        with self.assertRaisesRegex(ValueError, 'binary is missing'):
            dependency_notices(stage, assets, cache)


class WindowsDocumentationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)

    def document_fixture(self, root):
        documents = {
            'README.md': '# Repository\n',
            '.github/workflows/windows.yml': 'name: Windows\n',
            'docs/README.md': '[Windows](windows/README.md) · [macOS](macos/README.md) · [Common](common/README.md)',
            'docs/windows/README.md': '![Main](images/main.png)\n[Acceptance](ACCEPTANCE.md)',
            'docs/windows/ACCEPTANCE.md': '<img src="images/1.3.1/detail.png">\n[Capture](images/1.3.1/capture-manifest.json)',
            'docs/windows/images/main.png': 'main fixture',
            'docs/windows/images/1.3.1/detail.png': 'detail fixture',
            'docs/windows/images/1.3.1/capture-manifest.json': '{"data":"synthetic"}',
            'docs/macos/README.md': '[Docs](../README.md)',
            'docs/common/README.md': '![Detail](../windows/images/1.3.1/detail.png)',
        }
        files = []
        for name, content in documents.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding='utf-8')
            files.append(path)
        return files

    def extracted_fixture(self):
        self.document_fixture(self.root / 'source')
        (self.root / 'README.md').write_bytes(windows_package.release_readme())
        return self.root / 'source/docs/windows'

    def test_windows_archive_keeps_one_offline_documentation_tree(self):
        repository = self.root / 'repo'
        source_files = self.document_fixture(repository)
        (repository / 'LICENSE').write_text('fixture license', encoding='utf-8')
        app = self.root / 'app'
        app.mkdir()
        pe = bytearray(70)
        pe[:2] = b'MZ'
        struct.pack_into('<I', pe, 60, 64)
        pe[64:] = b'PE\0\0\x64\x86'
        app_files = {}
        for name, content in {'CodexUsage.exe': pe, 'coreclr.dll': pe, 'python/python.exe': pe,
                              'python/python314.dll': pe, 'python/python314._pth': PTH.encode()}.items():
            path = app / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
            app_files[name] = path
        manifest = dict(version=version(), architecture='win-x64', python=load_manifest(), dotnet_sdk=SDK_VERSION,
                        files={name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in app_files.items()},
                        sources={'docs/windows/README.md': hashlib.sha256((repository / 'docs/windows/README.md').read_bytes()).hexdigest()})
        manifest_path = app / 'BUILD-MANIFEST.json'
        manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
        app_files['BUILD-MANIFEST.json'] = manifest_path
        # Only native build validation is stubbed. ZIP generation/extraction,
        # inventories, checksums, PE headers and documentation checks are real.
        with patch.object(windows_package, 'ROOT', repository), patch.object(windows_package, 'windows_sources', return_value=source_files), \
                patch.object(windows_package, 'validate_app', return_value=(manifest, app_files)), patch('sys.stdout', new_callable=io.StringIO):
            archive = windows_package.package(app, self.root / 'dist')
        with patch.object(windows_verify, 'ROOT', repository), patch.object(windows_verify, 'windows_sources', return_value=source_files), \
                patch.object(windows_verify, 'validate_app'):
            count = windows_verify.inspect_archive(archive, self.root / 'extracted')
        with zipfile.ZipFile(archive) as package:
            names = {'/'.join(Path(name).parts[1:]) for name in package.namelist()}
        self.assertEqual(count, len(names))
        self.assertEqual(sorted(name for name in names if name.endswith('.png')),
                         ['source/docs/windows/images/1.3.1/detail.png', 'source/docs/windows/images/main.png'])
        self.assertFalse(any(name.startswith(('docs/', 'images/')) for name in names))
        self.assertIn('source/docs/windows/images/1.3.1/capture-manifest.json', names)
        self.assertIn('source/.github/workflows/windows.yml', names)
        self.assertNotIn('source/windows-ci.yml', names)

    def test_missing_nested_preview_is_rejected(self):
        docs = self.extracted_fixture()
        (docs / 'images/1.3.1/detail.png').unlink()
        with self.assertRaisesRegex(ValueError, 'documentation image'):
            windows_verify.verify_documentation_images(self.root)

    def test_broken_platform_navigation_or_capture_link_is_rejected(self):
        for missing in ('docs/macos/README.md', 'docs/windows/images/1.3.1/capture-manifest.json'):
            with self.subTest(missing=missing):
                self.extracted_fixture()
                (self.root / 'source' / missing).unlink()
                with self.assertRaisesRegex(ValueError, 'Broken documentation link'):
                    windows_verify.verify_documentation_images(self.root)

    def test_escaping_preview_is_rejected(self):
        docs = self.extracted_fixture()
        (docs / 'README.md').write_text('![Outside](../../../../../outside.png)', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'escaping documentation image'):
            windows_verify.verify_documentation_images(self.root)


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
