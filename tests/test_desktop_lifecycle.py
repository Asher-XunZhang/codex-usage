import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from urllib.parse import urlsplit
from urllib.request import ProxyHandler, build_opener

SERVER = Path(__file__).resolve().parents[1] / 'backend/dashboard_server.py'

class DesktopLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='codex desktop 生命周期 ')
        self.home = Path(self.temp.name)
        self.state = self.home / 'worker.json'
        self.children = []

    def tearDown(self):
        for child in self.children:
            if child.poll() is None:
                child.kill()
            child.wait(timeout=5)
        self.temp.cleanup()

    def command(self, parent):
        return [sys.executable, '-E', '-s', '-B', str(SERVER), '--port', '0',
                '--codex-home', str(self.home), '--state-file', str(self.state),
                '--instance-id', 'desktop-test', '--parent-pid', str(parent)]

    def ready(self):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                state = json.loads(self.state.read_text())
                with build_opener(ProxyHandler({})).open(state['url'] + '/health', timeout=1) as response:
                    health = json.load(response)
                if health['ready']:
                    self.assertEqual(health['instance_id'], 'desktop-test')
                    return state
            except (OSError, ValueError):
                pass
            time.sleep(.05)
        self.fail('worker did not become healthy')

    def assert_released(self, state):
        deadline = time.monotonic() + 5
        while self.state.exists() and time.monotonic() < deadline:
            time.sleep(.05)
        self.assertFalse(self.state.exists(), 'worker must remove its own state')
        port = urlsplit(state['url']).port
        with socket.socket() as sock:
            sock.settimeout(.5)
            self.assertNotEqual(sock.connect_ex(('127.0.0.1', port)), 0)

    def test_sigterm_releases_worker_state_and_port(self):
        child = subprocess.Popen(self.command(os.getpid()), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.children.append(child)
        state = self.ready()
        child.terminate()
        self.assertEqual(child.wait(timeout=5), 0)
        self.assert_released(state)

    def test_force_quit_parent_does_not_leave_orphan_listener(self):
        command = self.command(0)
        wrapper = ('import os, subprocess, sys, time; '
                   'args=sys.argv[1:]; args[-1]=str(os.getpid()); '
                   'child=subprocess.Popen(args); time.sleep(60)')
        parent = subprocess.Popen([sys.executable, '-E', '-s', '-B', '-c', wrapper, *command],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.children.append(parent)
        state = self.ready()
        try:
            parent.kill(); parent.wait(timeout=5)
            self.assert_released(state)
        finally:
            # Only the known test child may be signalled if the test failed.
            if self.state.exists():
                try:
                    os.kill(state['pid'], signal.SIGKILL)
                except ProcessLookupError:
                    pass

    def test_invalid_or_incompatible_parent_flags_fail(self):
        for extra in (['--parent-pid', '1'], ['--parent-pid', str(os.getpid()), '--supervise']):
            result = subprocess.run([sys.executable, '-E', '-s', '-B', str(SERVER), *extra], capture_output=True)
            self.assertEqual(result.returncode, 2)
            self.assertIn(b'--parent-pid', result.stderr)

if __name__ == '__main__':
    unittest.main()
