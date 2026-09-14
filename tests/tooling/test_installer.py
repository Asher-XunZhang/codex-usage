"""Offline installer transactions against temporary macOS files only.

Synthetic archives stand in for the pinned release. Bundle/signature checks are
stubbed for transaction tests; ditto, xattr and mv are the real system tools.
This does not establish Gatekeeper acceptance or Intel hardware compatibility.
"""
from tools.common.paths import ROOT, BACKEND, macos_source
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


INSTALLER = ROOT / 'scripts/install.sh'
APP_NAME = 'Codex用量.app'
QUARANTINE = 'com.apple.quarantine'


@unittest.skipUnless(Path('/bin/bash').is_file(), 'Requires Bash')
class InstallerSelectionTests(unittest.TestCase):
    def selection(self, hardware_arm64, reported_arch):
        code = '''
source "$INSTALLER"
sysctl() { printf '%s\\n' "$HARDWARE_ARM64"; }
uname() { printf '%s\\n' "$REPORTED_ARCH"; }
select_release
printf '%s\\n' "$architecture" "$label" "$expected_sha" "$release_url"
'''
        return subprocess.run(
            ['/bin/bash', '-c', code], text=True, capture_output=True,
            env=dict(os.environ, INSTALLER=str(INSTALLER),
                     HARDWARE_ARM64=hardware_arm64, REPORTED_ARCH=reported_arch),
            timeout=10,
        )

    def test_selects_pinned_release_for_intel_native_arm_and_rosetta(self):
        for hardware, reported, architecture, label, digest in [
            ('0', 'x86_64', 'x86_64', 'Intel',
             '9566a571450977b14b56f119cdd9cc2165d0ca20cbeb6070dc737b6daa84f695'),
            ('1', 'arm64', 'arm64', 'AppleSilicon',
             '630118772effc4272fb7a2f0ff757e9507a57b89be154f976b940deddc4286af'),
            ('1', 'x86_64', 'arm64', 'AppleSilicon',
             '630118772effc4272fb7a2f0ff757e9507a57b89be154f976b940deddc4286af'),
        ]:
            with self.subTest(hardware=hardware, reported=reported):
                result = self.selection(hardware, reported)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.splitlines(), [
                    architecture, label, digest,
                    'https://github.com/Asher-XunZhang/codex-usage/releases/'
                    f'download/v1.0.2/codex-usage-desktop-v1.0.2-{label}.zip',
                ])

    def test_intel_without_optional_arm64_key_uses_uname(self):
        result = self.selection('', 'x86_64')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines()[1], 'Intel')

    def test_unsupported_architecture_fails(self):
        result = self.selection('0', 'ppc64')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('不支持的芯片架构', result.stderr)

    def test_help_does_not_require_platform_checks_or_confirmation(self):
        result = subprocess.run(['/bin/bash', str(INSTALLER), '--help'],
                                text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('--archive ZIP', result.stdout)


@unittest.skipUnless(sys.platform == 'darwin' and os.geteuid() != 0,
                     'Requires macOS system tools and a non-root user')
class InstallerTransactionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='codex-install-test-')
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.destination = self.directory / 'Applications with spaces'
        self.target = self.destination / APP_NAME
        self.trace = self.directory / 'trace.txt'
        self.archive = self.directory / 'release with spaces.zip'
        source = self.directory / 'source' / 'Codex用量-1.0.2-Intel'
        self.fixture_app = source / APP_NAME
        self.payload = self.fixture_app / 'Contents' / 'payload.txt'
        self.payload.parent.mkdir(parents=True)
        self.payload.write_text('fixture app contents\n', encoding='utf-8')
        self.quarantine_value = b'0081;00000000;InstallerBehaviorTests;'
        self.set_attribute(self.fixture_app, QUARANTINE, self.quarantine_value)
        self.set_attribute(self.payload, QUARANTINE, self.quarantine_value)
        self.set_attribute(self.payload, 'org.codex-usage.test', b'preserve-me')
        subprocess.run(
            ['/usr/bin/ditto', '-c', '-k', '--sequesterRsrc', '--keepParent',
             str(source), str(self.archive)], check=True, capture_output=True,
        )
        self.digest = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        self.set_attribute(self.archive, QUARANTINE, self.quarantine_value)
        self.unrelated = self.directory / 'unrelated.txt'
        self.unrelated.write_text('keep this file\n', encoding='utf-8')
        self.set_attribute(self.unrelated, QUARANTINE, self.quarantine_value)

    @staticmethod
    def set_attribute(path, name, value):
        subprocess.run(['/usr/bin/xattr', '-wx', name, value.hex(), str(path)],
                       check=True, capture_output=True)

    @staticmethod
    def get_attribute(path, name):
        result = subprocess.run(['/usr/bin/xattr', '-px', name, str(path)],
                                check=True, text=True, capture_output=True)
        return bytes.fromhex(result.stdout)

    @staticmethod
    def list_attributes(path):
        result = subprocess.run(['/usr/bin/xattr', str(path)], check=True,
                                text=True, capture_output=True)
        return result.stdout.splitlines()

    def environment(self, **overrides):
        return dict(os.environ, INSTALLER=str(INSTALLER), FIXTURE_SHA=self.digest,
                    TRACE=str(self.trace), TARGET=str(self.target),
                    TEST_ROOT=str(self.directory), **overrides)

    def run_install(self, extra_setup='', *, digest=None, confirmation=True,
                    destination=None, archive=True, args=()):
        # Keep release selection real, changing only the digest to accept the
        # synthetic offline fixture. Never download or launch a real app.
        setup = '''
source "$INSTALLER"
eval "$(declare -f select_release | sed '1s/select_release/fixture_select_release/')"
select_release() { fixture_select_release; expected_sha="$FIXTURE_SHA"; }
sysctl() { printf '0\\n'; }
uname() { if [[ "$1" == -s ]]; then printf 'Darwin\\n'; else printf 'x86_64\\n'; fi; }
sw_vers() { printf '15.7.9\\n'; }
verify_bundle() {
    printf 'verify_bundle\\n' >> "$TRACE"
    [[ -f "$1/Contents/payload.txt" ]] || fail 'fixture bundle missing'
}
codesign() { printf 'codesign\\n' >> "$TRACE"; }
ditto() { printf 'ditto\\n' >> "$TRACE"; /usr/bin/ditto "$@"; }
xattr() { printf 'xattr\\n' >> "$TRACE"; /usr/bin/xattr "$@"; }
mv() { printf 'mv\\n' >> "$TRACE"; /bin/mv "$@"; }
curl() { printf 'unexpected network\\n' >> "$TRACE"; return 99; }
open() { printf 'unexpected open\\n' >> "$TRACE"; return 99; }
'''
        if confirmation:
            setup += 'confirm_install() { :; }\n'
        setup += extra_setup + '\ninstall_main "$@"\n'
        command = ['/bin/bash', '-c', setup, 'installer-test', '--destination',
                   str(destination or self.destination), '--no-open']
        if archive:
            command += ['--archive', str(self.archive)]
        command += list(args)
        environment = self.environment()
        if digest is not None:
            environment['FIXTURE_SHA'] = digest
        return subprocess.run(command, env=environment, text=True,
                              capture_output=True, timeout=20)

    def trace_lines(self):
        return self.trace.read_text().splitlines() if self.trace.exists() else []

    def assert_clean_transaction(self):
        if self.destination.exists():
            self.assertFalse((self.destination / '.codex-usage-install.lock').exists())
            self.assertEqual(list(self.destination.glob('.codex-usage-stage.*')), [])

    def assert_not_published(self, result):
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(self.target.exists())
        self.assertFalse(self.target.is_symlink())
        self.assert_clean_transaction()

    def test_offline_install_with_spaces_removes_only_app_quarantine(self):
        result = self.run_install()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        installed_payload = self.target / 'Contents' / 'payload.txt'
        self.assertEqual(installed_payload.read_bytes(), self.payload.read_bytes())
        self.assertNotIn(QUARANTINE, self.list_attributes(self.target))
        self.assertNotIn(QUARANTINE, self.list_attributes(installed_payload))
        self.assertEqual(self.get_attribute(installed_payload, 'org.codex-usage.test'), b'preserve-me')
        for original in (self.archive, self.fixture_app, self.payload, self.unrelated):
            self.assertEqual(self.get_attribute(original, QUARANTINE), self.quarantine_value)
        self.assertEqual(hashlib.sha256(self.archive.read_bytes()).hexdigest(), self.digest)
        self.assertEqual(self.trace_lines(), ['ditto', 'verify_bundle', 'xattr', 'codesign', 'mv'])
        self.assertEqual(list(self.destination.iterdir()), [self.target])
        self.assert_clean_transaction()

    def test_hash_mismatch_stops_before_extraction_and_cleans_temporary_files(self):
        result = self.run_install(digest='0' * 64)
        self.assert_not_published(result)
        self.assertIn('校验失败', result.stderr)
        self.assertEqual(self.trace_lines(), [])
        self.assertEqual(self.get_attribute(self.archive, QUARANTINE), self.quarantine_value)

    def test_no_tty_cannot_bypass_confirmation_and_creates_no_destination(self):
        result = self.run_install(confirmation=False)
        self.assert_not_published(result)
        self.assertIn('交互运行', result.stderr)
        self.assertFalse(self.destination.exists())
        self.assertEqual(self.trace_lines(), [])

    def test_real_entrypoint_rejects_no_tty_even_with_piped_install(self):
        result = subprocess.run(
            ['/bin/bash', str(INSTALLER), '--archive', str(self.archive),
             '--destination', str(self.destination), '--no-open'],
            input='install\n', text=True, capture_output=True, timeout=20,
        )
        self.assert_not_published(result)
        self.assertIn('交互运行', result.stderr)
        self.assertFalse(self.destination.exists())

    def test_tty_cancellation_leaves_destination_absent(self):
        import pty

        master, slave = pty.openpty()
        try:
            os.write(master, b'cancel\n')
            result = subprocess.run(
                ['/bin/bash', str(INSTALLER), '--archive', str(self.archive),
                 '--destination', str(self.destination), '--no-open'],
                stdin=slave, text=True, capture_output=True, timeout=20,
            )
        finally:
            os.close(slave)
            os.close(master)
        self.assert_not_published(result)
        self.assertIn('已取消安装', result.stderr)
        self.assertFalse(self.destination.exists())

    def test_invalid_options_paths_and_old_macos_leave_no_destination(self):
        for setup, options in [
            ('', {'args': ('--yes',)}),
            ('', {'args': ('--archive',)}),
            ('', {'destination': Path('relative-applications')}),
            ("sw_vers() { printf '10.15.7\\n'; }", {}),
            ("uname() { printf 'Linux\\n'; }", {}),
        ]:
            with self.subTest(setup=setup, options=options):
                result = self.run_install(setup, **options)
                self.assert_not_published(result)
                self.assertFalse(self.destination.exists())
                self.assertEqual(self.trace_lines(), [])

    def test_archive_symlink_is_rejected_without_touching_original(self):
        real_archive = self.directory / 'original.zip'
        self.archive.rename(real_archive)
        self.archive.symlink_to(real_archive)
        result = self.run_install()
        self.assert_not_published(result)
        self.assertFalse(self.destination.exists())
        self.assertTrue(self.archive.is_symlink())
        self.assertEqual(hashlib.sha256(real_archive.read_bytes()).hexdigest(), self.digest)
        self.assertEqual(self.get_attribute(real_archive, QUARANTINE), self.quarantine_value)

    def test_invalid_zip_with_matching_hash_fails_extraction_safely(self):
        self.archive.write_bytes(b'not a zip archive')
        result = self.run_install(digest=hashlib.sha256(self.archive.read_bytes()).hexdigest())
        self.assert_not_published(result)
        self.assertIn('解压失败', result.stderr)
        self.assertEqual(self.trace_lines(), ['ditto'])

    def test_existing_file_directory_and_symlink_are_preserved(self):
        self.destination.mkdir()
        for kind in ('file', 'directory', 'symlink', 'broken-symlink'):
            with self.subTest(kind=kind):
                if kind == 'file':
                    self.target.write_bytes(b'existing user file')
                elif kind == 'directory':
                    self.target.mkdir()
                    (self.target / 'existing').write_bytes(b'existing app data')
                else:
                    self.target.symlink_to(self.unrelated if kind == 'symlink'
                                           else self.directory / 'missing')
                result = self.run_install()
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('已有 App', result.stderr)
                self.assertEqual(self.trace_lines(), [])
                self.assert_clean_transaction()
                if kind == 'directory':
                    self.assertEqual((self.target / 'existing').read_bytes(), b'existing app data')
                    (self.target / 'existing').unlink()
                    self.target.rmdir()
                else:
                    if kind == 'file':
                        self.assertEqual(self.target.read_bytes(), b'existing user file')
                    else:
                        self.assertTrue(self.target.is_symlink())
                    self.target.unlink()

    def test_destination_symlink_and_regular_file_are_rejected(self):
        actual = self.directory / 'actual'
        actual.mkdir()
        self.destination.symlink_to(actual)
        result = self.run_install()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(list(actual.iterdir()), [])
        self.assertTrue(self.destination.is_symlink())
        self.destination.unlink()
        self.destination.write_bytes(b'existing file')
        result = self.run_install()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.destination.read_bytes(), b'existing file')
        self.assertEqual(self.trace_lines(), [])

    def test_existing_lock_is_not_removed(self):
        self.destination.mkdir()
        lock = self.destination / '.codex-usage-install.lock'
        lock.mkdir()
        result = self.run_install()
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue(lock.is_dir())
        self.assertEqual(list(self.destination.iterdir()), [lock])
        self.assertEqual(self.trace_lines(), [])

    def test_downstream_failures_and_signals_cleanup_without_publication(self):
        for setup, expected_code in [
            ("verify_bundle() { fail 'invalid bundle'; }", 1),
            ("xattr() { return 71; }", 1),
            ("codesign() { return 72; }", 1),
            ("mv() { return 73; }", 1),
            ("verify_bundle() { kill -TERM $$; }", 143),
            ("verify_bundle() { kill -INT $$; }", 130),
            ("verify_bundle() { kill -HUP $$; }", 129),
        ]:
            with self.subTest(setup=setup):
                result = self.run_install(setup)
                self.assert_not_published(result)
                self.assertEqual(result.returncode, expected_code, result.stderr)
                self.assertEqual(list(self.destination.iterdir()), [])

    def test_network_failure_cleans_lock_and_staging(self):
        result = self.run_install(archive=False)
        self.assert_not_published(result)
        self.assertIn('下载失败', result.stderr)
        self.assertEqual(self.trace_lines(), ['unexpected network'])

    def test_target_created_before_publication_check_is_preserved(self):
        result = self.run_install('''
codesign() { mkdir "$TARGET"; printf 'concurrent app' > "$TARGET/marker"; }
''')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('目标已出现', result.stderr)
        self.assertEqual((self.target / 'marker').read_text(), 'concurrent app')
        self.assertFalse((self.target / 'Contents').exists())
        self.assert_clean_transaction()

    def test_target_created_at_move_is_never_overwritten_or_nested(self):
        # Emulate a second process winning the final check/move race. The
        # wrapper inserts a target and still invokes macOS's real mv.
        scenarios = {
            'directory': 'mkdir "$TARGET"; printf existing > "$TARGET/marker"',
            'empty-directory': 'mkdir "$TARGET"',
            'file': 'printf existing > "$TARGET"',
            'symlink': 'ln -s "$TEST_ROOT/outside" "$TARGET"',
        }
        outside = self.directory / 'outside'
        outside.mkdir()
        (outside / 'marker').write_bytes(b'outside app')
        for kind, create in scenarios.items():
            with self.subTest(kind=kind):
                result = self.run_install('mv() { ' + create + '; /bin/mv "$@"; }')
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assert_clean_transaction()
                self.assertFalse((self.target / 'Contents').exists())
                self.assertFalse((self.target / APP_NAME).exists())
                self.assertEqual(list(outside.iterdir()), [outside / 'marker'])
                if kind == 'symlink':
                    self.assertTrue(self.target.is_symlink())
                    self.target.unlink()
                elif kind == 'file':
                    self.assertEqual(self.target.read_bytes(), b'existing')
                    self.target.unlink()
                else:
                    if kind == 'directory':
                        self.assertEqual((self.target / 'marker').read_bytes(), b'existing')
                        (self.target / 'marker').unlink()
                    self.target.rmdir()


if __name__ == '__main__':
    unittest.main()
