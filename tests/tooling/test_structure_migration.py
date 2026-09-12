"""Exercise consumers of the reorganized tree, including exported source without Git."""
import importlib
import json
import os
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import tempfile
import textwrap
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from tools.common.paths import ROOT, repository_root
from tools.common.distribution import source_files


class StructureMigrationTests(unittest.TestCase):
    def run_python(self, arguments, cwd):
        result = subprocess.run([sys.executable, '-E', '-s', '-B', *map(str, arguments)],
                                cwd=cwd, capture_output=True, text=True, encoding='utf-8', timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result.stdout

    def test_exported_source_discovers_every_test_and_runs_tooling_outside_checkout(self):
        with tempfile.TemporaryDirectory(prefix='source export 验证 ') as directory:
            outside = Path(directory)
            exported = outside / 'unpacked/source'
            for original in source_files(ROOT):
                target = exported / original.relative_to(ROOT)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(original, target)
            self.assertFalse((exported / '.git').exists())
            self.assertFalse((exported / 'build').exists())
            self.assertFalse((exported / 'dist').exists())
            self.assertEqual(repository_root(exported / 'tools/common/paths.py'), exported.resolve())
            # Discover from both trees independently: a missing test or import in the
            # payload cannot pass merely because producer/verifier share an allowlist.
            expected = json.loads(self.run_python([ROOT / 'test.py', '--list'], outside))
            actual = json.loads(self.run_python([exported / 'test.py', '--list'], outside))
            self.assertEqual(actual, expected)
            self.assertEqual(len(actual), len(set(actual)))
            # These consumers read the real resource manifests, licenses and project
            # version; discovery alone would not exercise those dependencies.
            self.run_python([exported / 'test.py', '--pattern', 'test_windows_build.py'], outside)

    def test_legacy_entries_alias_canonical_modules_and_forward_legacy_defaults(self):
        entries = {
            'build.py': 'tools.macos.build',
            'package.py': 'tools.macos.package',
            'scripts/fetch_runtime.py': 'tools.macos.fetch_runtime',
            'scripts/verify_release.py': 'tools.macos.verify_release',
            'scripts/build_windows.py': 'tools.windows.build',
            'scripts/fetch_windows_runtime.py': 'tools.windows.fetch_runtime',
            'scripts/package_windows.py': 'tools.windows.package',
            'scripts/verify_windows.py': 'tools.windows.verify_release',
            'scripts/test_windows_lifecycle.py': 'tools.windows.test_lifecycle',
            'scripts/test_windows_monitor.py': 'tools.windows.test_monitor',
            'scripts/measure_windows_resources.py': 'tools.windows.measure_resources',
        }
        for relative, canonical in entries.items():
            with self.subTest(entry=relative):
                implementation = importlib.import_module(canonical)
                old_name = relative.removesuffix('.py').replace('/', '.')
                previous = importlib.import_module(old_name)
                # Root macOS wrappers intentionally keep their old callable defaults;
                # scripts with identical callable APIs can alias the canonical module.
                if relative in ('build.py', 'package.py'):
                    self.assertIs(previous._implementation, implementation)
                else:
                    self.assertIs(previous, implementation)
                with patch.object(implementation, 'main', return_value=None) as main:
                    try:
                        runpy.run_path(str(ROOT / relative), run_name='__main__')
                    except SystemExit as exited:
                        self.assertIn(exited.code, (None, 0))
                if relative in ('build.py', 'package.py'):
                    main.assert_called_once_with(None, legacy=True)
                elif relative in ('scripts/fetch_windows_runtime.py', 'scripts/test_windows_lifecycle.py',
                                  'scripts/test_windows_monitor.py', 'scripts/measure_windows_resources.py'):
                    main.assert_called_once_with()
                else:
                    main.assert_called_once_with(legacy=True)

    def test_canonical_and_legacy_cli_help_work_from_unrelated_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            outside = Path(directory)
            for relative, module in (
                ('build.py', 'tools.macos.build'),
                ('package.py', 'tools.macos.package'),
                ('scripts/build_windows.py', 'tools.windows.build'),
                ('scripts/package_windows.py', 'tools.windows.package'),
            ):
                with self.subTest(entry=relative):
                    legacy = self.run_python([ROOT / relative, '--help'], outside)
                    # A module entry requires the source root on Python's import path;
                    # emulate a developer launcher without depending on its cwd/PYTHONPATH.
                    code = 'import runpy,sys; sys.path.insert(0,sys.argv.pop(1)); runpy.run_module(sys.argv.pop(1),run_name="__main__")'
                    canonical = self.run_python(['-c', code, ROOT, module, '--help'], outside)
                    self.assertIn('usage:', legacy)
                    self.assertIn('usage:', canonical)
            self.assertEqual(list(outside.iterdir()), [])

    def test_windows_build_outputs_allow_nested_artifacts_but_reject_sources_and_escape(self):
        import tools.windows.build as build
        with tempfile.TemporaryDirectory() as directory:
            # macOS exposes temporary paths through /var -> /private/var. Match
            # the canonical ROOT used by production without relaxing link checks.
            root = Path(directory).resolve()
            (root / 'build').mkdir()
            with patch.object(build, 'ROOT', root):
                for relative in ('build/windows/x64', 'build/windows/candidates/one', 'build/windows-candidate'):
                    self.assertEqual(build.owned_output(root / relative), root / relative)
                for relative in ('build', 'build/windows', 'src/windows', 'build/../outside'):
                    with self.subTest(path=relative), self.assertRaises(ValueError):
                        build.owned_output(root / relative)
                with patch('sys.argv', ['build']):
                    with patch.object(build, 'build') as invoked:
                        build.main()
                    self.assertEqual(invoked.call_args.args[0], root / 'build/windows/x64')
                    with patch.object(build, 'build') as invoked:
                        build.main(legacy=True)
                    self.assertEqual(invoked.call_args.args[0], root / 'build/windows-x64')

    def test_windows_build_output_rejects_a_linked_ancestor(self):
        import tools.windows.build as build
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            (root / 'build').mkdir()
            external = root / 'unrelated'
            external.mkdir()
            sentinel = external / 'keep.txt'
            sentinel.write_text('unrelated data', encoding='utf-8')
            try:
                (root / 'build/linked').symlink_to(external, target_is_directory=True)
            except OSError as error:
                if getattr(error, 'winerror', None) == 1314:
                    self.skipTest('Windows symlink privilege or Developer Mode is unavailable')
                raise
            with patch.object(build, 'ROOT', root), self.assertRaises(ValueError):
                build.owned_output(root / 'build/linked/candidate')
            self.assertEqual(sentinel.read_text(encoding='utf-8'), 'unrelated data')

    def test_ci_scope_preserves_shared_coverage_and_separates_platforms(self):
        workflow = (ROOT / '.github/workflows/windows.yml').read_text(encoding='utf-8')
        script = textwrap.dedent(workflow.split("python3 - <<'PY'\n", 1)[1].split('\n          PY', 1)[0])
        cases = [
            ('src/windows/App/Program.cs', 'push', '1', 0, {'windows'}),
            ('tools/windows/build.py', 'pull_request', '1', 0, {'windows'}),
            ('src/macos/App/Main.swift', 'push', '1', 0, {'macos'}),
            ('src/backend/disk_index.py', 'push', '1', 0, {'windows', 'macos'}),
            ('tests/macos/test_budget_core.py', 'push', '1', 0, {'windows', 'macos'}),
            ('tools/common/paths.py', 'push', '1', 0, {'windows', 'macos', 'docs'}),
            ('docs/windows/README.md', 'push', '1', 0, {'docs'}),
            ('website/index.md', 'push', '1', 0, set()),
            ('src/windows/App/Program.cs', 'workflow_dispatch', '', 0, {'windows', 'macos', 'docs'}),
            ('src/windows/App/Program.cs', 'push', '0' * 40, 0, {'windows', 'macos', 'docs'}),
            ('src/windows/App/Program.cs', 'push', 'missing', 128, {'windows', 'macos', 'docs'}),
        ]
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'outputs'
            for name, event, base, code, expected in cases:
                with self.subTest(path=name, event=event, base=base):
                    output.write_text('', encoding='utf-8')
                    with patch.dict(os.environ, BASE_REVISION=base, EVENT_NAME=event, GITHUB_OUTPUT=str(output)), \
                            patch('subprocess.run', return_value=SimpleNamespace(returncode=code, stdout=name.encode() + b'\0')):
                        exec(compile(script, 'workflow-scope', 'exec'), {})
                    values = dict(line.split('=', 1) for line in output.read_text(encoding='utf-8').splitlines())
                    self.assertEqual({key for key, value in values.items() if value == 'true'}, expected)


if __name__ == '__main__':
    unittest.main()
