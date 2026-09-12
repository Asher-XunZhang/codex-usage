"""Real Windows waits: no periodic timeout, cancellation and owned-handle cleanup."""
from tools.common.paths import ROOT, BACKEND, macos_source
import ctypes
import gc
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import MagicMock, patch

from parent_watch import ParentMonitor


@unittest.skipUnless(os.name == 'nt', 'Requires Windows process/event handles')
class WindowsParentWatchTests(unittest.TestCase):
    def monitor(self, **kwargs):
        return ParentMonitor(os.getppid(), threading.Event(), **kwargs)

    def test_idle_wait_is_single_infinite_call_and_stop_wakes_both_waiters(self):
        for compact in (False, True):
            with self.subTest(compact=compact), self.monitor(compact=compact) as monitor:
                entered = threading.Event()
                original = monitor.stop.kernel.WaitForMultipleObjects
                calls = []

                def wait(count, handles, all_handles, timeout):
                    calls.append((count, all_handles, timeout))
                    entered.set()
                    return original(count, handles, all_handles, timeout)

                with patch.object(monitor.stop.kernel, 'WaitForMultipleObjects', side_effect=wait):
                    monitor.start()
                    self.assertTrue(entered.wait(2))
                    self.assertFalse(monitor.stop.wait(.65))
                    self.assertTrue(monitor.thread.is_alive())
                    self.assertEqual(calls, [(2, False, 0xFFFFFFFF)])
                    monitor.stop.set()
                    self.assertTrue(monitor.stop.event.wait(1))
                    monitor.thread.join(2)
                    self.assertFalse(monitor.thread.is_alive())
                self.assertIsNone(monitor.stop.handle)

    def test_stop_before_start_keeps_initial_event_and_finishes(self):
        stop = threading.Event()
        stop.set()
        with ParentMonitor(os.getppid(), stop) as monitor:
            self.assertTrue(monitor.stop.is_set())
            monitor.start()
            monitor.thread.join(2)
            self.assertFalse(monitor.thread.is_alive())
        self.assertIsNone(monitor.stop.handle)

    def test_exception_in_worker_scope_cancels_and_joins(self):
        monitor = self.monitor()
        with self.assertRaisesRegex(ValueError, 'synthetic body'):
            with monitor:
                monitor.start()
                raise ValueError('synthetic body')
        self.assertFalse(monitor.thread.is_alive())
        self.assertIsNone(monitor.stop.handle)

    def test_start_exception_after_thread_started_does_not_close_live_handle(self):
        monitor = self.monitor()
        original = monitor.thread.start

        def interrupted_start():
            original()
            raise KeyboardInterrupt('synthetic start interruption')

        with patch.object(monitor.thread, 'start', side_effect=interrupted_start):
            with self.assertRaises(KeyboardInterrupt), monitor:
                monitor.start()
        self.assertFalse(monitor.thread.is_alive())
        self.assertIsNone(monitor.stop.handle)

    def test_unused_monitor_and_late_stop_are_safe(self):
        monitor = self.monitor()
        self.assertFalse(monitor.stop.finalizer.atexit)
        with monitor:
            pass
        self.assertIsNone(monitor.stop.handle)
        for _ in range(4):
            monitor.stop.set()
            monitor.stop.close()
        self.assertTrue(monitor.stop.wait(0))

    def test_late_request_set_can_race_owner_cleanup(self):
        monitor = self.monitor()
        done = threading.Event()
        failures = []

        def requester():
            try:
                while not done.is_set():
                    monitor.stop.set()
                    done.wait(.001)
            except BaseException as error:
                failures.append(error)

        with monitor:
            monitor.start()
            request = threading.Thread(target=requester)
            request.start()
        done.set()
        request.join(2)
        self.assertFalse(request.is_alive())
        self.assertFalse(failures)
        self.assertIsNone(monitor.stop.handle)

    def test_native_create_failure_is_synchronous(self):
        kernel = MagicMock()
        kernel.CreateEventW.return_value = 0
        with patch('ctypes.WinDLL', return_value=kernel), self.assertRaises(OSError):
            self.monitor()
        kernel.WaitForMultipleObjects.assert_not_called()

    def test_open_or_wait_failure_stops_service_and_releases_event(self):
        for method, outcome in (('OpenProcess', 0), ('OpenProcess', OSError('synthetic open')),
                                ('WaitForMultipleObjects', 0xFFFFFFFF), ('WaitForMultipleObjects', 258)):
            with self.subTest(method=method, outcome=str(outcome)):
                errors = []
                with self.monitor() as monitor, patch('threading.excepthook', side_effect=errors.append):
                    kwargs = {'side_effect': outcome} if isinstance(outcome, Exception) else {'return_value': outcome}
                    with patch.object(monitor.stop.kernel, method, **kwargs):
                        monitor.start()
                        monitor.thread.join(2)
                        self.assertFalse(monitor.thread.is_alive())
                        self.assertTrue(monitor.stop.is_set())
                self.assertIsNone(monitor.stop.handle)
                self.assertEqual(len(errors), 0 if outcome == 0 else 1)

    def test_repeated_lifetimes_do_not_accumulate_handles(self):
        from ctypes import wintypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.GetCurrentProcess.restype = wintypes.HANDLE
        kernel.GetProcessHandleCount.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        kernel.GetProcessHandleCount.restype = wintypes.BOOL

        def count():
            value = wintypes.DWORD()
            self.assertTrue(kernel.GetProcessHandleCount(kernel.GetCurrentProcess(), ctypes.byref(value)))
            return value.value

        def cycle():
            with self.monitor() as monitor:
                monitor.start()

        for _ in range(8):
            cycle()
        gc.collect()
        before = count()
        for _ in range(100):
            cycle()
        gc.collect()
        self.assertLessEqual(count(), before + 1)

    def test_compact_entry_stops_when_real_parent_is_killed(self):
        # Block a synthetic scan in the real compact entry point so its parent
        # definitely exits while monitoring is active. Never read user logs.
        with tempfile.TemporaryDirectory(prefix='parent wait ') as directory:
            root = Path(directory)
            backend = BACKEND
            ready = root / 'ready'
            completed = root / 'completed'
            code = '''import sys, threading
from pathlib import Path
sys.path.insert(0, sys.argv.pop(1))
ready, completed = Path(sys.argv.pop(1)), Path(sys.argv.pop(1))
import compact_snapshot
class Index:
    def __init__(self, *args): self.stop = threading.Event()
    def scan(self):
        ready.write_text('ready')
        if not self.stop.wait(15): raise RuntimeError('parent exit was not detected')
        raise InterruptedError()
compact_snapshot.DiskUsageIndex = Index
compact_snapshot.main()
completed.write_text('completed')
'''
            command = [sys.executable, '-E', '-s', '-B', '-c', code, str(backend), str(ready), str(completed),
                       '--codex-home', str(root), '--cache-path', str(root / 'unused.sqlite'),
                       '--output', str(root / 'unused.json'), '--parent-pid', '0']
            wrapper = ('import os, subprocess, sys, time; args=sys.argv[1:]; args[-1]=str(os.getpid()); '
                       'child=subprocess.Popen(args, creationflags=subprocess.CREATE_NO_WINDOW); '
                       'print(child.pid, flush=True); time.sleep(30)')
            parent = subprocess.Popen([sys.executable, '-E', '-s', '-B', '-c', wrapper, *command],
                                      stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                      creationflags=subprocess.CREATE_NO_WINDOW)
            child_pid = int(parent.stdout.readline())
            try:
                deadline = time.monotonic() + 5
                while not ready.exists() and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertTrue(ready.exists())
                parent.kill()
                parent.wait(3)
                deadline = time.monotonic() + 5
                while not completed.exists() and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertTrue(completed.exists(), 'compact monitor did not stop the synthetic scan')
            finally:
                if parent.poll() is None:
                    parent.kill()
                parent.wait(3)
                parent.stdout.close()
                if not completed.exists():
                    try:
                        os.kill(child_pid, 15)
                    except ProcessLookupError:
                        pass


if __name__ == '__main__':
    unittest.main()
