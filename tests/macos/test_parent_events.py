"""Real macOS event waits, cancellation races and descriptor ownership."""
import errno
import gc
import os
from pathlib import Path
import select
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch
from urllib.request import ProxyHandler, build_opener

import dashboard_server
from dashboard_data import UsageIndex
from dashboard_server import LocalHTTPServer, make_handler
import parent_watch
from parent_watch import ParentMonitor
from tools.common.paths import BACKEND


@unittest.skipUnless(sys.platform == 'darwin', 'Requires macOS process events')
class MacOSParentEventsTests(unittest.TestCase):
    def monitor(self, **kwargs):
        return ParentMonitor(os.getppid(), threading.Event(), **kwargs)

    def test_http_waits_for_request_or_stop_without_idle_timeout(self):
        original = dashboard_server.selectors.DefaultSelector
        calls = []
        entered = threading.Event()

        class Selector(original):
            def select(self, timeout=None):
                calls.append(timeout)
                entered.set()
                return super().select(timeout)

        with tempfile.TemporaryDirectory() as directory:
            index = UsageIndex(Path(directory), refresh_seconds=0)
            with ParentMonitor(None, index.stop) as monitor:
                index.stop = monitor.stop
                with LocalHTTPServer(('127.0.0.1', 0), make_handler(index)) as server, \
                        patch.object(dashboard_server.selectors, 'DefaultSelector', Selector):
                    worker = threading.Thread(target=server.serve_until_stopped, args=(index.stop,), daemon=True)
                    worker.start()
                    try:
                        self.assertTrue(entered.wait(2))
                        self.assertFalse(index.stop.wait(.65))
                        self.assertEqual(calls, [None])
                        opener = build_opener(ProxyHandler({}))
                        for _ in range(2):
                            with opener.open(f'http://127.0.0.1:{server.server_port}/health', timeout=2) as response:
                                self.assertEqual(response.status, 200)
                        self.assertTrue(all(timeout is None for timeout in calls))
                    finally:
                        index.stop.set()
                        worker.join(2)
                        self.assertFalse(worker.is_alive())

    def test_idle_is_one_infinite_wait_and_stop_wakes_all_consumers(self):
        original = select.kqueue
        for compact in (False, True):
            calls = []
            entered = threading.Event()

            class Queue:
                def __init__(self):
                    self.queue = original()

                def control(self, changes, count, timeout):
                    if changes is None:
                        calls.append(timeout)
                        entered.set()
                    return self.queue.control(changes, count, timeout)

                def close(self):
                    self.queue.close()

            with self.subTest(compact=compact), self.monitor(compact=compact) as monitor:
                with patch.object(select, 'kqueue', Queue):
                    monitor.start()
                    self.assertTrue(entered.wait(2))
                    self.assertFalse(monitor.stop.wait(.65))
                    self.assertEqual(calls, [None])
                    self.assertTrue(monitor.thread.is_alive())
                    monitor.stop.set()
                    self.assertTrue(monitor.stop.event.wait(1))
                    monitor.thread.join(2)
                    self.assertFalse(monitor.thread.is_alive())
                # The HTTP owner can still observe the same stop after the
                # parent waiter exits. Nothing drains or closes its pipe early.
                for _ in range(2):
                    self.assertEqual(select.select([monitor.stop], [], [], 0)[0], [monitor.stop])
                self.assertFalse(os.get_inheritable(monitor.stop.fileno()))
            self.assertIsNone(monitor.stop.fileno())

    def test_stop_before_start_and_without_parent(self):
        for parent in (None, os.getppid()):
            with self.subTest(parent=parent):
                event = threading.Event()
                event.set()
                with ParentMonitor(parent, event) as monitor:
                    self.assertTrue(monitor.stop.is_set())
                    self.assertEqual(select.select([monitor.stop], [], [], 0)[0], [monitor.stop])
                    monitor.start()
                    if monitor.thread:
                        monitor.thread.join(2)
                        self.assertFalse(monitor.thread.is_alive())
                self.assertIsNone(monitor.stop.fileno())

    def test_unused_monitor_exception_and_interrupted_start_release_pipe(self):
        for mode in ('unused', 'body', 'start'):
            with self.subTest(mode=mode):
                monitor = self.monitor()
                start = monitor.thread.start

                def interrupted():
                    start()
                    raise KeyboardInterrupt('synthetic start')

                try:
                    with monitor:
                        if mode == 'start':
                            with patch.object(monitor.thread, 'start', side_effect=interrupted):
                                monitor.start()
                        elif mode == 'body':
                            monitor.start()
                            raise ValueError('synthetic body')
                except (ValueError, KeyboardInterrupt):
                    pass
                self.assertFalse(monitor.thread.is_alive())
                self.assertIsNone(monitor.stop.fileno())
                monitor.stop.set()
                monitor.stop.close()

    def test_parent_identity_checked_before_and_after_registration(self):
        for identities, registrations in (([1], 0), ([42, 1], 1)):
            with self.subTest(identities=identities), self.monitor() as monitor:
                queue = MagicMock()
                with patch.object(os, 'getppid', side_effect=identities), \
                        patch.object(select, 'kqueue', return_value=queue):
                    parent_watch.watch_parent(42, monitor.stop)
                self.assertTrue(monitor.stop.is_set())
                self.assertEqual(queue.control.call_count, registrations)
                if registrations:
                    queue.close.assert_called_once()

    def test_interrupted_join_does_not_close_borrowed_pipe(self):
        monitor = self.monitor()
        release = threading.Event()
        entered = threading.Event()

        def borrowing_waiter(*_):
            entered.set()
            release.wait(3)

        monitor.target = borrowing_waiter
        monitor.start()
        self.assertTrue(entered.wait(2))
        try:
            with patch.object(monitor.thread, 'join', side_effect=KeyboardInterrupt('synthetic join')):
                with self.assertRaises(KeyboardInterrupt):
                    monitor.__exit__()
            self.assertTrue(monitor.thread.is_alive())
            os.fstat(monitor.stop.fileno())
        finally:
            release.set()
            monitor.thread.join(2)
            monitor.stop.close()

    def test_registration_or_wait_error_stops_worker_and_releases_queue(self):
        for failure in ('gone', 'register', 'wait', 'event'):
            with self.subTest(failure=failure), self.monitor() as monitor:
                queue = MagicMock()
                code = errno.ESRCH if failure == 'gone' else errno.EBADF
                if failure in ('gone', 'register'):
                    queue.control.side_effect = OSError(code, 'synthetic registration')
                elif failure == 'wait':
                    queue.control.side_effect = [[], OSError(code, 'synthetic wait')]
                else:
                    queue.control.side_effect = [[], [SimpleNamespace(flags=select.KQ_EV_ERROR, data=code)]]
                with patch.object(select, 'kqueue', return_value=queue):
                    if failure == 'gone':
                        parent_watch.watch_parent(os.getppid(), monitor.stop)
                    else:
                        with self.assertRaises(OSError):
                            parent_watch.watch_parent(os.getppid(), monitor.stop)
                self.assertTrue(monitor.stop.is_set())
                queue.close.assert_called_once()

    def test_native_creation_failures_do_not_leave_running_worker_or_pipe(self):
        with patch.object(os, 'pipe', side_effect=OSError(errno.EMFILE, 'synthetic pipe')):
            with self.assertRaises(OSError):
                self.monitor()
        descriptors = os.pipe()
        with patch.object(os, 'pipe', return_value=descriptors), \
                patch.object(os, 'set_blocking', side_effect=OSError(errno.EBADF, 'synthetic flags')):
            with self.assertRaises(OSError):
                self.monitor()
        for descriptor in descriptors:
            with self.assertRaises(OSError):
                os.fstat(descriptor)
        with self.monitor() as monitor, patch.object(select, 'kqueue', side_effect=OSError(errno.EMFILE, 'synthetic queue')):
            with self.assertRaises(OSError):
                parent_watch.watch_parent(os.getppid(), monitor.stop)
            self.assertTrue(monitor.stop.is_set())

    def test_repeated_lifetimes_and_late_stop_do_not_leak_descriptors(self):
        gc.collect()
        before = len(os.listdir('/dev/fd'))
        failures = []
        for _ in range(100):
            monitor = self.monitor()
            go = threading.Event()

            def late_stop():
                go.wait(2)
                try:
                    for _ in range(20):
                        monitor.stop.set()
                except BaseException as error:
                    failures.append(error)

            with monitor:
                monitor.start()
                requester = threading.Thread(target=late_stop)
                requester.start()
                go.set()
            requester.join(2)
            self.assertFalse(requester.is_alive())
            self.assertFalse(monitor.thread.is_alive())
            self.assertIsNone(monitor.stop.fileno())
        gc.collect()
        self.assertFalse(failures)
        self.assertEqual(len(os.listdir('/dev/fd')), before)

    def test_compact_entry_cancels_when_real_parent_exits(self):
        for killed in (False, True):
            with self.subTest(killed=killed), tempfile.TemporaryDirectory(prefix='mac parent ') as directory:
                root = Path(directory)
                ready, completed = root / 'ready', root / 'completed'
                code = '''import sys, threading
from pathlib import Path
sys.path.insert(0, sys.argv.pop(1))
ready, completed = Path(sys.argv.pop(1)), Path(sys.argv.pop(1))
import compact_snapshot
class Index:
    def __init__(self, *args): self.stop = threading.Event()
    def scan(self):
        ready.write_text('ready')
        if not self.stop.wait(10): raise RuntimeError('Parent exit was not detected')
        raise InterruptedError()
compact_snapshot.DiskUsageIndex = Index
compact_snapshot.main()
completed.write_text('completed')
'''
                command = [sys.executable, '-E', '-s', '-B', '-c', code, str(BACKEND), str(ready), str(completed),
                           '--codex-home', str(root), '--cache-path', str(root / 'unused.sqlite'),
                           '--output', str(root / 'unused.json'), '--parent-pid', '0']
                wrapper = ('import os, subprocess, sys; args=sys.argv[1:]; args[-1]=str(os.getpid()); '
                           'child=subprocess.Popen(args, stdin=subprocess.DEVNULL); '
                           'print(child.pid, flush=True); sys.stdin.buffer.read(1)')
                with (root / 'stderr').open('w') as errors:
                    parent = subprocess.Popen([sys.executable, '-E', '-s', '-B', '-c', wrapper, *command],
                                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
                    child_pid = int(parent.stdout.readline())
                    try:
                        deadline = time.monotonic() + 3
                        while not ready.exists() and time.monotonic() < deadline:
                            time.sleep(.01)
                        self.assertTrue(ready.exists())
                        if killed:
                            parent.kill()
                        else:
                            parent.stdin.close()
                        parent.wait(3)
                        deadline = time.monotonic() + 3
                        while not completed.exists() and time.monotonic() < deadline:
                            time.sleep(.01)
                        self.assertTrue(completed.exists(), (root / 'stderr').read_text())
                        self.assertFalse((root / 'unused.json').exists())
                    finally:
                        if parent.poll() is None:
                            parent.kill()
                        parent.wait(3)
                        parent.stdin.close(); parent.stdout.close()
                        if not completed.exists():
                            try:
                                os.kill(child_pid, 9)
                            except ProcessLookupError:
                                pass


if __name__ == '__main__':
    unittest.main()
