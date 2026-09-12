from tools.common.paths import ROOT, BACKEND, macos_source
import json
import os
import secrets
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from urllib.parse import urlsplit
from urllib.request import ProxyHandler, Request, build_opener
from urllib.error import HTTPError

SERVER = BACKEND / 'dashboard_server.py'

class DesktopLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='codex desktop 生命周期 ')
        self.home = Path(self.temp.name)
        self.state = self.home / 'worker.json'
        self.children = []
        self.worker_log = self.home / 'worker.log'
        self.log_stream = self.worker_log.open('ab')
        self.control_token = secrets.token_hex(32)
        self.worker_env = dict(os.environ, CODEX_USAGE_BACKEND_CONTROL_TOKEN=self.control_token)

    def tearDown(self):
        for child in self.children:
            if child.poll() is None:
                child.kill()
            child.wait(timeout=5)
        self.log_stream.close()
        self.temp.cleanup()

    def command(self, parent):
        bootstrap = ('import faulthandler, os, runpy, sys; '
                     'faulthandler.dump_traceback_later(5); '
                     'sys.argv=sys.argv[1:]; sys.path.insert(0, os.path.dirname(sys.argv[0])); '
                     'runpy.run_path(sys.argv[0], run_name="__main__")')
        return [sys.executable, '-E', '-s', '-B', '-c', bootstrap, str(SERVER), '--port', '0',
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
        self.fail('worker did not become healthy; process states=' +
                  repr([child.poll() for child in self.children]) + '\n' +
                  self.worker_log.read_text(encoding='utf-8', errors='replace')[-12000:])

    def assert_released(self, state):
        deadline = time.monotonic() + 5
        while self.state.exists() and time.monotonic() < deadline:
            time.sleep(.05)
        self.assertFalse(self.state.exists(), 'worker must remove its own state')
        port = urlsplit(state['url']).port
        with socket.socket() as sock:
            sock.settimeout(.5)
            self.assertNotEqual(sock.connect_ex(('127.0.0.1', port)), 0)

    def test_graceful_shutdown_releases_worker_state_and_port(self):
        child = subprocess.Popen(self.command(os.getpid()), stdout=self.log_stream, stderr=self.log_stream, env=self.worker_env)
        self.children.append(child)
        state = self.ready()
        if os.name == 'nt':
            request = Request(state['url'] + '/api/shutdown', data=b'{}',
                              headers={'X-Codex-Instance': 'desktop-test', 'X-Codex-Control': self.control_token})
            with build_opener(ProxyHandler({})).open(request, timeout=2) as response:
                self.assertTrue(json.load(response)['stopping'])
        else:
            child.terminate()
        self.assertEqual(child.wait(timeout=5), 0)
        self.assert_released(state)

    def test_shutdown_rejects_wrong_instance_and_foreign_origin(self):
        child = subprocess.Popen(self.command(os.getpid()), stdout=self.log_stream, stderr=self.log_stream, env=self.worker_env)
        self.children.append(child)
        state = self.ready()
        opener = build_opener(ProxyHandler({}))
        for headers in ({}, {'X-Codex-Instance': 'other'},
                        {'X-Codex-Instance': 'desktop-test', 'Origin': 'https://example.invalid'}):
            request = Request(state['url'] + '/api/shutdown', data=b'{}', headers=headers)
            with self.assertRaises(HTTPError) as error:
                opener.open(request, timeout=2)
            self.assertEqual(error.exception.code, 403)
            error.exception.close()
            self.assertIsNone(child.poll())

    def test_public_health_identity_cannot_authorize_shutdown(self):
        child = subprocess.Popen(self.command(os.getpid()), stdout=self.log_stream, stderr=self.log_stream, env=self.worker_env)
        self.children.append(child)
        state = self.ready()
        opener = build_opener(ProxyHandler({}))
        with opener.open(state['url'] + '/health', timeout=2) as response:
            health = json.load(response)
        self.assertNotIn(self.control_token, json.dumps(health))
        self.assertNotIn(self.control_token, self.state.read_text())
        for control in (None, health['instance_id'], '0' * 64):
            headers = {'X-Codex-Instance': health['instance_id']}
            if control is not None:
                headers['X-Codex-Control'] = control
            with self.assertRaises(HTTPError) as error:
                opener.open(Request(state['url'] + '/api/shutdown', data=b'{}', headers=headers), timeout=2)
            self.assertEqual(error.exception.code, 403 if os.name == 'nt' else 404)
            error.exception.close()
            self.assertIsNone(child.poll())

    def test_worker_without_private_control_channel_has_no_shutdown_endpoint(self):
        env = dict(os.environ)
        env.pop('CODEX_USAGE_BACKEND_CONTROL_TOKEN', None)
        child = subprocess.Popen(self.command(os.getpid()), stdout=self.log_stream, stderr=self.log_stream, env=env)
        self.children.append(child)
        state = self.ready()
        with self.assertRaises(HTTPError) as error:
            build_opener(ProxyHandler({})).open(Request(state['url'] + '/api/shutdown', data=b'{}',
                headers={'X-Codex-Instance': 'desktop-test', 'X-Codex-Control': self.control_token}), timeout=2)
        self.assertEqual(error.exception.code, 404)
        error.exception.close()
        self.assertIsNone(child.poll())

    @unittest.skipIf(os.name == 'nt', 'Windows requires direct-parent validation before startup')
    def test_unix_missing_parent_uses_watcher_instead_of_new_startup_rejection(self):
        try:
            result = subprocess.run(self.command(2147483647), stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10)
        except subprocess.TimeoutExpired as exc:
            self.fail('worker failed to stop:\n' + (exc.stderr or b'').decode(errors='replace'))
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
        self.assertFalse(self.state.exists())

    def test_force_quit_parent_does_not_leave_orphan_listener(self):
        command = self.command(0)
        wrapper = ('import os, subprocess, sys, time; '
                   'args=sys.argv[1:]; args[-1]=str(os.getpid()); '
                   'child=subprocess.Popen(args); time.sleep(60)')
        parent = subprocess.Popen([sys.executable, '-E', '-s', '-B', '-c', wrapper, *command],
                                  stdout=self.log_stream, stderr=self.log_stream)
        self.children.append(parent)
        state = self.ready()
        try:
            parent.kill(); parent.wait(timeout=5)
            self.assert_released(state)
        finally:
            # Only the known test child may be signalled if the test failed.
            if self.state.exists():
                try:
                    os.kill(state['pid'], signal.SIGTERM if os.name == 'nt' else signal.SIGKILL)
                except ProcessLookupError:
                    pass

    def test_invalid_or_incompatible_parent_flags_fail(self):
        for extra in (['--parent-pid', '1'], ['--parent-pid', str(os.getpid()), '--supervise']):
            result = subprocess.run([sys.executable, '-E', '-s', '-B', str(SERVER), *extra], capture_output=True)
            self.assertEqual(result.returncode, 2)
            self.assertIn(b'--parent-pid', result.stderr)

    @unittest.skipUnless(os.name == 'nt', 'Windows direct-parent startup validation')
    def test_windows_rejects_valid_pid_that_is_not_direct_parent(self):
        result = subprocess.run(self.command(os.getppid()), capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 2)
        self.assertIn(b'--parent-pid', result.stderr)
        self.assertFalse(self.state.exists())

if __name__ == '__main__':
    unittest.main()
