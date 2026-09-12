"""Offline checks for pinned downloads and release boundary validation."""
from tools.common.paths import ROOT, BACKEND, macos_source
from tools.common.distribution import METADATA_FILES
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(ROOT))
from tools.macos.package import VERSION, documentation_files, source_files
from tools.macos.fetch_runtime import fetch_runtime, load_manifest, selected_entries
from tools.macos.verify_release import inspect_archive


class RuntimeDownloadTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.addCleanup(self.directory.cleanup)
        self.payload = b'pinned-runtime-payload'
        self.entry = {'file': 'runtime.tar.gz', 'url': 'https://example.invalid/runtime.tar.gz',
                      'bytes': len(self.payload), 'sha256': hashlib.sha256(self.payload).hexdigest()}

    def test_verified_atomic_download_and_cache_reuse(self):
        target, fetched = fetch_runtime(self.root, self.entry, lambda *a, **k: io.BytesIO(self.payload))
        self.assertTrue(fetched)
        self.assertEqual(target.read_bytes(), self.payload)
        if os.name != 'nt':
            self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o644)
        self.assertTrue(os.access(target, os.R_OK | os.W_OK))
        with patch('tools.macos.fetch_runtime.urlopen', side_effect=AssertionError('must not download')):
            self.assertEqual(fetch_runtime(self.root, self.entry), (target, False))
        self.assertEqual([path.name for path in self.root.iterdir()], [self.entry['file']])

    def test_bad_existing_cache_is_never_overwritten(self):
        target = self.root / self.entry['file']
        target.write_bytes(b'other-data')
        with patch('tools.macos.fetch_runtime.urlopen', side_effect=AssertionError('must not download')):
            with self.assertRaisesRegex(ValueError, 'left untouched'):
                fetch_runtime(self.root, self.entry)
        self.assertEqual(target.read_bytes(), b'other-data')

    def test_truncation_oversize_and_wrong_hash_leave_no_final_or_temp_file(self):
        for payload in (self.payload[:-1], self.payload + b'extra', b'x' * len(self.payload)):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                fetch_runtime(self.root, self.entry, lambda *a, **k: io.BytesIO(payload))
            self.assertEqual(list(self.root.iterdir()), [])

    def test_interrupted_download_cleans_partial_file(self):
        class Interrupted(io.BytesIO):
            def read(self, size):
                raise OSError('connection interrupted')
        with self.assertRaises(OSError):
            fetch_runtime(self.root, self.entry, lambda *a, **k: Interrupted())
        self.assertEqual(list(self.root.iterdir()), [])

    def test_concurrent_bad_cache_survives_atomic_publication(self):
        target = self.root / self.entry['file']
        def concurrent_writer(*args, **kwargs):
            target.write_bytes(b'concurrent-other-data')
            return io.BytesIO(self.payload)
        with self.assertRaisesRegex(ValueError, 'left untouched'):
            fetch_runtime(self.root, self.entry, concurrent_writer)
        self.assertEqual(target.read_bytes(), b'concurrent-other-data')
        self.assertEqual(list(self.root.iterdir()), [target])

    def test_cache_symlink_is_rejected(self):
        original = self.root / 'original'
        original.write_bytes(self.payload)
        try:
            (self.root / self.entry['file']).symlink_to(original)
        except OSError as error:
            if getattr(error, 'winerror', None) == 1314:
                self.skipTest('Windows symlink privilege or Developer Mode is unavailable')
            raise
        with self.assertRaisesRegex(ValueError, 'regular file'):
            fetch_runtime(self.root, self.entry)
        self.assertEqual(original.read_bytes(), self.payload)

    def test_pinned_manifest_all_architectures_and_offline_subset(self):
        entries = load_manifest(ROOT / 'resources/macos/runtimes/manifest.json', require_all=True)
        self.assertEqual(len(selected_entries(entries, 'universal')), 2)
        for arch, marker in [('arm64', 'aarch64'), ('x86_64', 'x86_64')]:
            selected = selected_entries(entries, arch)
            self.assertEqual(len(selected), 1)
            self.assertIn(marker, selected[0]['file'])
            manifest = self.root / 'manifest.json'
            manifest.write_text(json.dumps(selected))
            self.assertEqual(load_manifest(manifest), selected)
            with self.assertRaises(ValueError):
                load_manifest(manifest, require_all=True)

    def test_manifest_rejects_url_override_path_escape_duplicate_and_bad_size(self):
        entries = load_manifest(ROOT / 'resources/macos/runtimes/manifest.json', require_all=True)
        variants = []
        for field, value in [('url', 'https://other.invalid/runtime'), ('file', '../runtime.tar.gz'),
                             ('bytes', True), ('bytes', -1), ('sha256', 'invalid')]:
            modified = copy.deepcopy(entries)
            modified[0][field] = value
            variants.append(modified)
        variants.append([entries[0], entries[0]])
        for variant in variants:
            manifest = self.root / 'manifest.json'
            manifest.write_text(json.dumps(variant))
            with self.assertRaises(ValueError):
                load_manifest(manifest)


class ReleaseBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.addCleanup(self.directory.cleanup)

    def archive(self, name='README.md', payload=b'documentation', mode=stat.S_IFREG | 0o644, bad_hash=False):
        target = self.root / 'test.zip'
        prefix = f'Codex用量-{VERSION}-AppleSilicon'
        sums = {name: '0' * 64 if bad_hash else hashlib.sha256(payload).hexdigest()}
        with zipfile.ZipFile(target, 'w') as archive:
            for member, data, permissions in [(name, payload, mode), ('SHA256SUMS.json', json.dumps(sums).encode(), stat.S_IFREG | 0o644)]:
                info = zipfile.ZipInfo(prefix + '/' + member)
                info.create_system = 3
                info.external_attr = permissions << 16
                archive.writestr(info, data)
        return target

    def test_archive_preserves_regular_file_permissions_and_inventory(self):
        target = self.archive('binary', b'binary', stat.S_IFREG | 0o755)
        output = self.root / 'extracted'
        result = inspect_archive(target, output, 'AppleSilicon')
        self.assertEqual(result, {'binary', 'SHA256SUMS.json'})
        if os.name != 'nt':
            self.assertEqual(stat.S_IMODE((output / 'binary').stat().st_mode), 0o755)
        self.assertEqual((output / 'binary').read_bytes(), b'binary')

    def test_archive_rejects_traversal_private_paths_and_symlinks(self):
        for name, mode in [('..\x2fescape', stat.S_IFREG | 0o644), ('auth.json', stat.S_IFREG | 0o644),
                           ('session.jsonl', stat.S_IFREG | 0o644), ('link', stat.S_IFLNK | 0o777),
                           ('world-write', stat.S_IFREG | 0o666)]:
            with self.subTest(name=name):
                target = self.archive(name=name, mode=mode)
                with self.assertRaises(ValueError):
                    inspect_archive(target, self.root / ('extract-' + name.replace('/', '-')), 'AppleSilicon')
        self.assertFalse((self.root / 'escape').exists())

    def test_archive_rejects_tampered_checksum(self):
        with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
            inspect_archive(self.archive(bad_hash=True), self.root / 'extracted', 'AppleSilicon')

    def test_source_allowlist_keeps_skill_manifests_and_licenses_but_excludes_runtime_and_logs(self):
        required = ['README.md', 'LICENSE', 'THIRD-PARTY.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'build.py', 'test.py', 'package.py', 'pytest.ini',
                    'resources/macos/runtimes/manifest.json', 'resources/macos/THIRD-PARTY.md',
                    'resources/windows/runtimes/manifest.json',
                    'resources/macos/third-party-licenses/LICENSE', 'integrations/codex-token-usage/SKILL.md',
                    'integrations/codex-token-usage/agents/openai.yaml', 'integrations/codex-token-usage/scripts/token_usage.py', 'docs/README.md',
                    'docs/macos/README.md', 'docs/macos/VALIDATION.md', 'docs/macos/images/demo.png',
                    'docs/windows/README.md', 'docs/windows/images/1.3.1/demo.png',
                    'docs/windows/images/1.3.1/capture-manifest.json', 'docs/common/README.md',
                    'docs/common/images/icon.png',
                    'tools/macos/preview/main.swift', 'scripts/install.sh', 'tools/macos/install.sh',
                    'src/backend/token_usage.py', 'src/macos/App/Main.swift', 'src/windows/App/Program.cs',
                    'tests/common/test_example.py', 'tools/common/paths.py', *sorted(METADATA_FILES)]
        excluded = ['resources/macos/runtimes/runtime.tar.gz', 'tests/__pycache__/cache.pyc',
                    'tests/private.log', 'src/backend/local.sqlite', 'docs/windows/images/1.3.1/private.json',
                    'docs/windows/images/other/capture-manifest.json', 'docs/macos/capture-manifest.json',
                    '.github/workflows/private.yml', '.agents/private.md',
                    'src/windows/obj/Generated.cs', 'src/windows/bin/Generated.cs',
                    'build/windows/x64/example.cs', 'dist/macos/private.py']
        for name in required + excluded:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('fixture')
        collected = {path.relative_to(self.root).as_posix() for path in source_files(self.root)}
        self.assertEqual(collected, set(required))
        self.assertEqual({path.relative_to(self.root).as_posix() for path in documentation_files(self.root)},
                         {name for name in required if name.startswith('docs/')})


if __name__ == '__main__':
    unittest.main()
