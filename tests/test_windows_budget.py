"""Run the macOS budget core's original behavioral assertions against C# on Windows.

Build first, then run with an explicit binary to avoid testing an old local build:
    $env:CODEX_USAGE_WINDOWS_EXE = (Resolve-Path build/windows-x64/CodexUsage.exe)
    python -m unittest discover -s tests -p test_windows_budget.py -v

No Swift compiler or third-party test package is required. The assertions and
fixtures are inherited unchanged from test_budget_core.py; only the JSON runner
is replaced. Production --self-test also runs independent C# budget checks.
"""
import json
import os
from pathlib import Path
import subprocess
import unittest
import uuid

import test_budget_core as mac_budget_tests


EXE = os.environ.get('CODEX_USAGE_WINDOWS_EXE', '')


@unittest.skipUnless(os.name == 'nt' and EXE, 'Set CODEX_USAGE_WINDOWS_EXE to the newly built Windows executable')
class WindowsBudgetParityTests(mac_budget_tests.BudgetCoreTests):
    __unittest_skip__ = False
    __unittest_skip_why__ = ''

    @classmethod
    def setUpClass(cls):
        cls.binary = Path(EXE).resolve()
        if not cls.binary.is_file():
            raise RuntimeError(f'Windows budget parity executable is missing; rebuild first: {cls.binary}')

    @classmethod
    def tearDownClass(cls):
        pass

    def run_commands(self, *commands, path=None):
        unique = uuid.uuid4().hex
        input_path = Path(self.directory.name) / f'commands-{unique}.json'
        output_path = Path(self.directory.name) / f'results-{unique}.json'
        input_path.write_text(json.dumps({'path': str(path or self.path), 'commands': commands}), encoding='utf-8')
        completed = subprocess.run(
            [str(self.binary), '--budget-harness', '--input', str(input_path), '--output', str(output_path)],
            capture_output=True, text=True, timeout=60,
        )
        self.assertEqual(completed.returncode, 0, completed.stderr or completed.stdout)
        self.assertTrue(output_path.is_file(), 'Budget harness did not write a result; rebuild the Windows executable with --budget-harness support')
        result = json.loads(output_path.read_text(encoding='utf-8-sig'))
        self.assertEqual(result.get('version'), 1, 'Unexpected budget harness version; rebuild the Windows executable')
        return result['results']


if __name__ == '__main__':
    unittest.main()
