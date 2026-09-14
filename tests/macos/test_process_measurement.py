"""Check resource accounting units and process-identity boundaries."""
import ctypes
import os
import sys
import time
import unittest

from tools.macos.measure_processes import (COUNTERS, Usage, counter_summary,
                                         mach_timebase, read_fd_count, read_usage)


def counters(identity, user=0, system=0, **extra):
    return (dict.fromkeys(COUNTERS, 0)
            | dict(start_abstime=identity, user_time=user, system_time=system, **extra))


class ProcessCounterTests(unittest.TestCase):
    def test_pid_reuse_does_not_merge_lifetimes_or_lifetime_totals(self):
        samples = [dict(process_counters={'4': counters(10, 100)}),
                   dict(process_counters={'4': counters(10, 124, 24)}),
                   dict(process_counters={'4': counters(20, 900)}),
                   dict(process_counters={'4': counters(20, 912)})]
        rows = counter_summary(samples, (125, 3))
        self.assertEqual(len(rows), 2)
        self.assertAlmostEqual(sum(row['cpu_seconds'] for row in rows), 0.0000025)
        self.assertEqual([row['observations'] for row in rows], [2, 2])

    def test_counter_regression_is_invalid_not_zero(self):
        samples = [dict(process_counters={'4': counters(10, 100)}),
                   dict(process_counters={'4': counters(10, 90)}),
                   dict(process_counters={'4': counters(10, 110)})]
        row = counter_summary(samples, (1, 1))[0]
        self.assertFalse(row['valid'])
        self.assertIsNone(row['cpu_seconds'])
        self.assertIsNone(row['delta'])

    def test_counts_and_io_are_deltas_not_scaled_time(self):
        a = counters(10, pkg_idle_wkups=2, diskio_bytesread=1024)
        b = counters(10, pkg_idle_wkups=5, diskio_bytesread=3072)
        row = counter_summary([dict(process_counters={'4': a}),
                               dict(process_counters={'4': b})], (125, 3))[0]
        self.assertEqual(row['delta']['pkg_idle_wkups'], 3)
        self.assertEqual(row['delta']['diskio_bytesread'], 2048)

    @unittest.skipUnless(sys.platform == 'darwin', 'Requires macOS libproc')
    def test_live_cpu_units_match_python_process_time_and_fd_changes(self):
        library = ctypes.CDLL('/usr/lib/libproc.dylib')
        library.proc_pid_rusage.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
        library.proc_pid_rusage.restype = ctypes.c_int
        self.assertEqual(ctypes.sizeof(Usage), 160)
        before = read_usage(library, os.getpid())
        started = time.process_time()
        while time.process_time() - started < 0.08:
            sum(range(1000))
        elapsed = time.process_time() - started
        after = read_usage(library, os.getpid())
        numer, denom = mach_timebase()
        cpu = ((after.user_time + after.system_time - before.user_time - before.system_time)
               * numer / denom / 1e9)
        self.assertAlmostEqual(cpu, elapsed, delta=0.025)
        baseline = read_fd_count(library, os.getpid())
        self.assertIsNotNone(baseline)
        with open(os.devnull) as first, open(os.devnull) as second:
            self.assertGreaterEqual(read_fd_count(library, os.getpid()), baseline + 2)
        self.assertEqual(read_fd_count(library, os.getpid()), baseline)


if __name__ == '__main__':
    unittest.main()
