"""Darwin ABI and race boundaries for the read-only process sampler."""
import ctypes
import hashlib
import itertools
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from scripts import measure_processes as measurement


def fake_library():
    return Mock(proc_listallpids=Mock(), proc_pidinfo=Mock(), proc_pidpath=Mock(), proc_pid_rusage=Mock())


class ProcessInventoryTests(unittest.TestCase):
    def test_pid_count_is_converted_to_byte_capacity_and_full_buffer_retries(self):
        library = fake_library()
        sizes = []
        def list_pids(buffer, byte_count):
            sizes.append(byte_count)
            if buffer is None:
                return 2
            capacity = byte_count // ctypes.sizeof(ctypes.c_int)
            for index in range(capacity):
                buffer[index] = index + 1
            return capacity if len(sizes) == 2 else 129
        library.proc_listallpids.side_effect = list_pids
        inventory = measurement.ProcessInventory(library)
        self.assertEqual(inventory._list_pids(), list(range(1, 130)))
        self.assertEqual(sizes, [0, 128 * 4, 256 * 4])
        self.assertEqual(inventory._list_pids(), list(range(1, 130)))
        self.assertEqual(sizes[-1], 256 * 4)

    def test_negative_enumeration_returns_raise(self):
        for returns in ([-1], [1, -1]):
            library = fake_library()
            library.proc_listallpids.side_effect = returns
            with self.assertRaises(OSError):
                measurement.ProcessInventory(library)._list_pids()

    def test_oversized_count_does_not_index_outside_pid_buffer(self):
        library = fake_library()
        for results in ([1, 2_000_000], [2_000_000]):
            library.proc_listallpids.side_effect = results
            with self.assertRaisesRegex(RuntimeError, 'buffer size'):
                measurement.ProcessInventory(library)._list_pids()

    def test_partial_bsd_info_and_missing_path_are_handled_without_losing_known_parent_edge(self):
        library = fake_library()
        inventory = measurement.ProcessInventory(library)
        inventory._list_pids = Mock(return_value=[10, 11, 12, 13])
        path = '/Applications/演示.app/Contents/MacOS/Test'.encode()
        def pid_info(pid, flavor, argument, output, size):
            self.assertEqual((flavor, argument, size), (3, 0, 136))
            value = ctypes.cast(output, ctypes.POINTER(measurement.BSDInfo)).contents
            value.pid = pid if pid != 12 else 99
            value.ppid = 1 if pid == 10 else 10
            return size - 4 if pid == 11 else size
        def pid_path(pid, output, size):
            self.assertEqual(size, 4096)
            if pid == 13:
                return 0
            ctypes.memmove(output, path + b'\0', len(path) + 1)
            return len(path)
        library.proc_pidinfo.side_effect = pid_info
        library.proc_pidpath.side_effect = pid_path
        table = inventory.table()
        self.assertEqual(table, {10: (1, os.fsdecode(path)), 13: (10, '')})
        self.assertEqual(measurement.descendants(table, {10}), {10, 13})
        self.assertEqual(inventory.last_diagnostics,
                         {'listed_pids': 4, 'bsd_info_unreadable': 1, 'path_unreadable': 1, 'bsd_pid_mismatch': 1})

    def test_out_of_bounds_path_length_is_not_read(self):
        library = fake_library()
        inventory = measurement.ProcessInventory(library)
        inventory._list_pids = Mock(return_value=[10])
        def pid_info(pid, flavor, argument, output, size):
            value = ctypes.cast(output, ctypes.POINTER(measurement.BSDInfo)).contents
            value.pid = pid
            value.ppid = 1
            return size
        library.proc_pidinfo.side_effect = pid_info
        library.proc_pidpath.return_value = 4096
        self.assertEqual(inventory.table(), {10: (1, '')})
        self.assertEqual(inventory.last_diagnostics['path_unreadable'], 1)

    def test_unreadable_rusage_is_missing_not_zero(self):
        library = fake_library()
        library.proc_pid_rusage.return_value = -1
        self.assertIsNone(measurement.read_usage(library, 42))

    def test_descendant_tracking_terminates_for_cycle(self):
        self.assertEqual(measurement.descendants({1: (2, ''), 2: (1, ''), 3: (2, '')}, {1}), {1, 2, 3})

    @unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Darwin SDK required')
    def test_ctypes_struct_sizes_and_offsets_match_local_darwin_headers(self):
        source = r'''#include <stdio.h>
#include <stddef.h>
#include <libproc.h>
#include <sys/proc_info.h>
#include <sys/resource.h>
int main(void) { printf("%zu %zu %zu %zu %d %zu\n", sizeof(pid_t), sizeof(struct proc_bsdinfo),
 offsetof(struct proc_bsdinfo,pbi_ppid), offsetof(struct proc_bsdinfo,pbi_start_tvsec),
 PROC_PIDPATHINFO_MAXSIZE, sizeof(struct rusage_info_v2)); }
'''
        with tempfile.TemporaryDirectory(prefix='codex-usage-abi-test-') as directory:
            root = Path(directory)
            (root / 'sizes.c').write_text(source)
            subprocess.run(['xcrun', 'clang', str(root / 'sizes.c'), '-o', str(root / 'sizes')],
                           check=True, capture_output=True)
            actual = [int(part) for part in subprocess.check_output([str(root / 'sizes')], text=True).split()]
        self.assertEqual(actual, [ctypes.sizeof(ctypes.c_int), ctypes.sizeof(measurement.BSDInfo),
                                  measurement.BSDInfo.ppid.offset, measurement.BSDInfo.start_seconds.offset,
                                  measurement.PROC_PIDPATHINFO_MAXSIZE, ctypes.sizeof(measurement.Usage)])


class MeasurementReportTests(unittest.TestCase):
    def prefs(self):
        values = {key: None for key in measurement.PREFERENCE_KEYS}
        values.update(displayMode='floating', refreshSeconds=0, capsuleTheme='light')
        return {'readable': True, 'values': values}

    def run_mock_measure(self, include_main=False, identity_changes=False):
        app = Path('/fixture/Codex.app')
        host_path = str(app / 'Contents/MacOS/CodexUsage')
        table = {10: (1, host_path)}
        if include_main:
            table.update({20: (1, str(app / 'Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage')),
                          30: (20, '/fixture/python')})
        inventory = Mock(library=fake_library(), table=Mock(return_value=table), last_diagnostics={})
        reads = {}
        def usage(library, pid):
            reads[pid] = reads.get(pid, 0) + 1
            value = measurement.Usage()
            value.proc_start_abstime = 100 + (1 if identity_changes and reads[pid] > 1 else 0)
            value.phys_footprint = pid * measurement.MIB
            return value
        with patch.object(measurement, 'ProcessInventory', return_value=inventory), \
             patch.object(measurement, 'preferences', side_effect=[self.prefs(), self.prefs()]), \
             patch.object(measurement, 'build_metadata', return_value={'version': 'test'}), \
             patch.object(measurement, 'system_metadata', return_value={'sampler_architecture': 'test'}), \
             patch.object(measurement, 'read_usage', side_effect=usage), \
             patch.object(measurement.time, 'monotonic', side_effect=itertools.count(0, 0.01)):
            return measurement.measure(app, 0, 0.2, scenario='controlled fixture', source_revision='explicit-label')

    def test_host_only_name_alias_and_explicit_experiment_labels(self):
        result, samples = self.run_mock_measure()
        self.assertTrue(result['valid'])
        self.assertEqual(result['host_only_mib_median'], 10)
        self.assertEqual(result['idle_host_mib_median'], 10)
        self.assertIn('idle_host_mib_median', result['deprecated_fields'])
        self.assertEqual(result['scenario'], 'controlled fixture')
        self.assertEqual(result['source_revision'], 'explicit-label')
        self.assertEqual(result['actual_sample_intervals_seconds']['count'], 0)
        self.assertEqual(len(samples), 1)

    def test_independent_main_and_its_child_are_included(self):
        result, samples = self.run_mock_measure(include_main=True)
        self.assertTrue(result['valid'])
        self.assertIsNone(result['host_only_mib_median'])
        self.assertEqual(result['combined_mib_median'], 60)
        self.assertEqual(samples[0]['related_pids'], [20, 30])

    def test_gui_pid_reuse_invalidates_run(self):
        result, _ = self.run_mock_measure(identity_changes=True)
        self.assertFalse(result['valid'])
        self.assertIn('gui_process_identity_changed', result['invalid_reasons'])

    def test_reparented_child_with_unreadable_usage_invalidates_and_remains_tracked(self):
        app = Path('/fixture/Codex.app')
        host = (1, str(app / 'Contents/MacOS/CodexUsage'))
        tables = [
            {10: host, 30: (10, '/fixture/child')},  # initial inventory
            {10: host, 30: (10, '/fixture/child')},
            {10: host, 30: (1, '/fixture/child')},   # reparented, rusage unavailable
            {10: host, 30: (1, '/fixture/child')},   # rusage recovers
            {10: host, 30: (1, '/fixture/child')},   # final inventory
        ]
        clock = Mock(return_value=0)
        inventory = Mock(library=fake_library(), last_diagnostics={})
        calls = 0

        def table():
            nonlocal calls
            value = tables[calls]
            clock.return_value = max(0, calls - 1)
            calls += 1
            return value

        child_reads = 0

        def usage(library, pid):
            nonlocal child_reads
            if pid == 30:
                child_reads += 1
                if child_reads == 2:
                    return None
            value = measurement.Usage()
            value.proc_start_abstime = pid
            value.phys_footprint = pid * measurement.MIB
            return value

        inventory.table.side_effect = table
        with patch.object(measurement, 'ProcessInventory', return_value=inventory), \
             patch.object(measurement, 'preferences', side_effect=[self.prefs(), self.prefs()]), \
             patch.object(measurement, 'build_metadata', return_value={'version': 'test'}), \
             patch.object(measurement, 'system_metadata', return_value={}), \
             patch.object(measurement, 'read_usage', side_effect=usage), \
             patch.object(measurement.time, 'monotonic', clock), \
             patch.object(measurement.time, 'sleep'):
            result, samples = measurement.measure(app, 2, 0.2)
        self.assertFalse(result['valid'])
        self.assertIn('some_observed_processes_unreadable', result['invalid_reasons'])
        self.assertEqual(samples[1]['unreadable_current_pids'], [30])
        self.assertEqual(samples[2]['related_pids'], [30])
        self.assertEqual(samples[2]['combined_mib'], 40)
        self.assertIsNone(result['host_only_mib_median'])
        self.assertEqual(result['sampled_host_only_percent'], 0)

    def test_preference_export_keeps_only_allowlisted_fields(self):
        payload = plistlib.dumps({'capsuleTheme': 'dark', 'floatingTask': 'fixture-task',
                                  'private-unknown-setting': 'must-not-be-recorded'})
        with patch.object(measurement.subprocess, 'run', return_value=Mock(returncode=0, stdout=payload)):
            result = measurement.preferences()
        self.assertTrue(result['readable'])
        self.assertEqual(result['values']['floatingTask'], 'fixture-task')
        self.assertNotIn('private-unknown-setting', result['values'])

    def test_preference_export_failure_is_explicit(self):
        with patch.object(measurement.subprocess, 'run', return_value=Mock(returncode=1, stdout=b'')):
            self.assertFalse(measurement.preferences()['readable'])

    def test_actual_interval_summary_includes_slow_sample(self):
        actual = measurement.interval_statistics([0.2, 0.21, 0.6])
        self.assertEqual(actual['median'], 0.21)
        self.assertEqual(actual['max'], 0.6)
        self.assertEqual(actual['p95'], 0.561)

    def test_build_identity_hashes_both_guis_and_helpers(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory)
            files = ['Contents/MacOS/' + name for name in ('CodexUsage', 'CodexSummary', 'CodexQuota')]
            files.append('Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage')
            for name in files:
                path = app / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(name.encode())
            (app / 'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleShortVersionString': '1.6.0',
                                                                    'CFBundleVersion': '160'}))
            result = measurement.build_metadata(app)
            self.assertEqual(result['version'], '1.6.0')
            self.assertEqual(result['build'], '160')
            self.assertEqual(result['binary_sha256'], {name: hashlib.sha256(name.encode()).hexdigest() for name in files})


if __name__ == '__main__':
    unittest.main()
