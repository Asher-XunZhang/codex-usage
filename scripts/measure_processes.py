#!/usr/bin/env python3
"""Sample an already-running macOS app and its related processes without UI actions.

Reports observed physical-footprint samples, not a guaranteed lifetime peak.
CPU is deliberately omitted; these counters are not used to infer energy or responsiveness.
"""
import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
import platform
import plistlib
from pathlib import Path
import statistics
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
MIB = 1024 ** 2


class Usage(ctypes.Structure):
    """Darwin rusage_info_v2, as declared in sys/resource.h."""
    _fields_ = [('uuid', ctypes.c_uint8 * 16)] + [
        (name, ctypes.c_uint64) for name in (
            'user_time', 'system_time', 'pkg_idle_wkups', 'interrupt_wkups',
            'pageins', 'wired_size', 'resident_size', 'phys_footprint',
            'proc_start_abstime', 'proc_exit_abstime', 'child_user_time',
            'child_system_time', 'child_pkg_idle_wkups', 'child_interrupt_wkups',
            'child_pageins', 'child_elapsed_abstime', 'diskio_bytesread',
            'diskio_byteswritten')]


# Darwin ABI: libproc.h; sys/proc_info.h; sys/resource.h. The return
# value from proc_listallpids is a PID COUNT, but its buffer size is in BYTES.
# Apple implementation: xnu/libsyscall/wrappers/libproc/libproc.c.
PROC_PIDTBSDINFO = 3
PROC_PIDPATHINFO_MAXSIZE = 4096


class BSDInfo(ctypes.Structure):
    """Darwin proc_bsdinfo (136 bytes on supported arm64/x86_64 macOS)."""
    _fields_ = [(name, ctypes.c_uint32) for name in (
        'flags', 'status', 'xstatus', 'pid', 'ppid', 'uid', 'gid', 'ruid',
        'rgid', 'svuid', 'svgid', 'reserved')]
    _fields_ += [('comm', ctypes.c_char * 16), ('name', ctypes.c_char * 32)]
    _fields_ += [(name, ctypes.c_uint32) for name in (
        'nfiles', 'pgid', 'pjobc', 'tdev', 'tpgid')]
    _fields_ += [('nice', ctypes.c_int32), ('start_seconds', ctypes.c_uint64),
                 ('start_microseconds', ctypes.c_uint64)]


class ProcessInventory:
    """Read executable paths and PPIDs directly, never process arguments."""
    def __init__(self, library=None):
        self.library = library or ctypes.CDLL('/usr/lib/libproc.dylib', use_errno=True)
        self.library.proc_listallpids.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.library.proc_listallpids.restype = ctypes.c_int
        self.library.proc_pidinfo.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64,
                                              ctypes.c_void_p, ctypes.c_int]
        self.library.proc_pidinfo.restype = ctypes.c_int
        self.library.proc_pidpath.argtypes = [ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32]
        self.library.proc_pidpath.restype = ctypes.c_int
        if ctypes.sizeof(ctypes.c_int) != 4 or ctypes.sizeof(BSDInfo) != 136 or BSDInfo.ppid.offset != 16:
            raise RuntimeError('Unsupported Darwin libproc ABI')
        self._pids = None
        self.last_diagnostics = {}

    def _list_pids(self):
        if self._pids is None:
            count = self.library.proc_listallpids(None, 0)
            if count < 0:
                raise OSError(ctypes.get_errno(), 'proc_listallpids sizing failed')
            if count > 999_936:
                raise RuntimeError('Unreasonable libproc PID buffer size')
            self._pids = (ctypes.c_int * max(128, count + 64))()
        for _ in range(8):
            capacity = len(self._pids)
            count = self.library.proc_listallpids(self._pids, ctypes.sizeof(self._pids))
            if count < 0:
                raise OSError(ctypes.get_errno(), 'proc_listallpids enumeration failed')
            if count < capacity:
                return sorted({int(pid) for pid in self._pids[:count] if pid > 0})
            # An exact fill may be truncated: do not silently accept it.
            new_capacity = max(capacity * 2, count + 64)
            if new_capacity > 1_000_000:
                raise RuntimeError('Unreasonable libproc PID buffer size')
            self._pids = (ctypes.c_int * new_capacity)()
        raise RuntimeError('Process inventory kept growing during enumeration')

    def table(self):
        table = {}
        diagnostics = {'listed_pids': 0, 'bsd_info_unreadable': 0,
                       'path_unreadable': 0, 'bsd_pid_mismatch': 0}
        pids = self._list_pids()
        diagnostics['listed_pids'] = len(pids)
        path_buffer = ctypes.create_string_buffer(PROC_PIDPATHINFO_MAXSIZE)
        for pid in pids:
            info = BSDInfo()
            size = self.library.proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, ctypes.byref(info), ctypes.sizeof(info))
            if size != ctypes.sizeof(info):
                # Processes can exit while enumerating; never read partial structs.
                diagnostics['bsd_info_unreadable'] += 1
                continue
            if info.pid != pid:
                diagnostics['bsd_pid_mismatch'] += 1
                continue
            length = self.library.proc_pidpath(pid, path_buffer, ctypes.sizeof(path_buffer))
            if 0 < length < ctypes.sizeof(path_buffer):
                command = os.fsdecode(path_buffer.raw[:length])
            else:
                command = ''
                diagnostics['path_unreadable'] += 1
            # A protected executable can lack a path yet still be an intermediate
            # ancestor. Keep the known PPID edge instead of dropping that process.
            table[pid] = (int(info.ppid), command)
        self.last_diagnostics = diagnostics
        return table


def descendants(table, roots):
    by_parent = {}
    for pid, (parent, _) in table.items():
        by_parent.setdefault(parent, []).append(pid)
    found = set(roots)
    pending = list(roots)
    while pending:
        for pid in by_parent.get(pending.pop(), ()):
            if pid not in found:
                found.add(pid)
                pending.append(pid)
    return found


PREFERENCE_KEYS = ('displayMode', 'refreshSeconds', 'capsuleTheme', 'floatingVisible',
                   'floatingPinned', 'floatingDays', 'floatingModel', 'floatingTask',
                   'filterDays', 'filterModel', 'filterTask', 'filterGroup')


def preferences():
    # Two exports per run, never a subprocess in the sampling loop. Parse only
    # explicitly allowed settings; never store the full preference domain.
    result = subprocess.run(
        ['/usr/bin/defaults', 'export', 'local.codex-usage.desktop', '-'],
        capture_output=True, timeout=5)
    if result.returncode != 0:
        return {'readable': False, 'values': {key: None for key in PREFERENCE_KEYS}}
    try:
        values = plistlib.loads(result.stdout)
        if not isinstance(values, dict):
            raise ValueError('Preference domain is not a dictionary')
    except (ValueError, plistlib.InvalidFileException):
        return {'readable': False, 'values': {key: None for key in PREFERENCE_KEYS}}
    return {'readable': True, 'values': {key: values.get(key) for key in PREFERENCE_KEYS}}


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for data in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(data)
    return digest.hexdigest()


def build_metadata(app):
    info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
    relative_binaries = [f'Contents/MacOS/{name}' for name in ('CodexUsage', 'CodexSummary', 'CodexQuota')]
    relative_binaries.append('Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage')
    return {'version': info.get('CFBundleShortVersionString'), 'build': info.get('CFBundleVersion'),
            'bundle_identifier': info.get('CFBundleIdentifier'),
            'binary_sha256': {name: file_sha256(app / name) for name in relative_binaries}}


def system_metadata(app):
    architectures = subprocess.check_output(
        ['/usr/bin/lipo', '-archs', str(app / 'Contents/MacOS/CodexUsage')], text=True, timeout=5).split()
    os_build = subprocess.check_output(['/usr/bin/sw_vers', '-buildVersion'], text=True, timeout=5).strip()
    return {'macos_version': platform.mac_ver()[0], 'macos_build': os_build,
            'kernel_release': platform.release(), 'sampler_architecture': platform.machine(),
            'host_binary_architectures': architectures}


def interval_statistics(values):
    if not values:
        return {'count': 0, 'min': None, 'median': None, 'mean': None, 'p95': None, 'max': None}
    values = sorted(values)
    position = (len(values) - 1) * 0.95
    low = int(position)
    high = min(low + 1, len(values) - 1)
    p95 = values[low] + (values[high] - values[low]) * (position - low)
    return {'count': len(values), 'min': round(values[0], 6), 'median': round(statistics.median(values), 6),
            'mean': round(statistics.mean(values), 6), 'p95': round(p95, 6), 'max': round(values[-1], 6)}


def read_usage(library, pid):
    value = Usage()
    return value if library.proc_pid_rusage(pid, 2, ctypes.byref(value)) == 0 else None


def save_json(path, value):
    with path.open('x', encoding='utf-8') as output:
        path.chmod(0o600)
        json.dump(value, output, ensure_ascii=False, indent=2)
        output.write('\n')


def measure(app, duration, interval, scenario=None, source_revision=None):
    inventory = ProcessInventory()
    library = inventory.library
    library.proc_pid_rusage.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
    library.proc_pid_rusage.restype = ctypes.c_int
    host_path = str(app / 'Contents/MacOS/CodexUsage')
    main_path = str(app / 'Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage')
    gui_paths = {host_path: 'host', main_path: 'main'}

    def gui_pids(table):
        return {pid: gui_paths[command] for pid, (_, command) in table.items()
                if command in gui_paths}

    initial_build = build_metadata(app)
    environment = system_metadata(app)
    initial_table = inventory.table()
    initial_guis = gui_pids(initial_table)
    if sum(role == 'host' for role in initial_guis.values()) != 1:
        raise RuntimeError('Exactly one host must already be running from --app; no app was launched.')
    start_preferences = preferences()
    start_mode = start_preferences['values']['displayMode']
    start_interval = start_preferences['values']['refreshSeconds']
    tracked = {}
    initial_identities = {}
    issues = set()
    for pid in initial_guis:
        usage = read_usage(library, pid)
        if usage is None:
            raise RuntimeError('A GUI process became unreadable before sampling began.')
        initial_identities[pid] = usage.proc_start_abstime
        tracked[pid] = usage.proc_start_abstime

    samples = []
    observed_roles = dict(initial_guis)
    began = time.monotonic()
    while True:
        sample_began = time.monotonic()
        table = inventory.table()
        inventory_duration = time.monotonic() - sample_began
        guis = gui_pids(table)
        observed_roles.update(guis)
        if guis != initial_guis:
            issues.add('gui_process_set_changed')
        # Include independently launched Main even if launchd is its parent.
        current = descendants(table, guis)
        # Keep previously observed descendants through reparenting, but exclude
        # recycled PIDs using their kernel process-start identity.
        known = set(tracked)
        measured = {}
        unreadable = []
        for pid in sorted(current | known):
            usage = read_usage(library, pid)
            if usage is None:
                if pid in current or pid in table:
                    # A known descendant may have reparented to launchd. A
                    # readable BSD entry means it is still observed, so missing
                    # rusage is not proof of exit and must not silently lose it.
                    unreadable.append(pid)
                else:
                    tracked.pop(pid, None)
                continue
            identity = usage.proc_start_abstime
            if pid in known and tracked[pid] != identity and pid not in current:
                tracked.pop(pid, None)
                continue
            if pid in initial_identities and identity != initial_identities[pid]:
                issues.add('gui_process_identity_changed')
            tracked[pid] = identity
            measured[pid] = usage.phys_footprint
        if any(pid not in measured for pid in initial_guis):
            issues.add('initial_gui_unreadable_or_exited')
        host_pids = [pid for pid, role in guis.items() if role == 'host']
        host = measured.get(host_pids[0]) if len(host_pids) == 1 else None
        related = sorted(pid for pid in measured if pid not in host_pids)
        samples.append({
            'seconds': round(time.monotonic() - began, 6),
            'inventory_seconds': round(inventory_duration, 6),
            'inventory_diagnostics': dict(inventory.last_diagnostics),
            'gui_processes': guis,
            'host_mib': host / MIB if host is not None else None,
            'combined_mib': sum(measured.values()) / MIB,
            'process_mib': {str(pid): value / MIB for pid, value in measured.items()},
            'related_pids': related,
            'unreadable_current_pids': unreadable,
        })
        if time.monotonic() - began >= duration:
            break
        time.sleep(min(interval, max(0, duration - (time.monotonic() - began))))
    final_guis = gui_pids(inventory.table())
    if final_guis != initial_guis:
        issues.add('gui_process_set_changed')
    end_preferences = preferences()
    end_mode = end_preferences['values']['displayMode']
    end_interval = end_preferences['values']['refreshSeconds']
    final_build = build_metadata(app)
    if initial_build != final_build:
        issues.add('app_build_changed')
    if not start_preferences['readable'] or not end_preferences['readable']:
        issues.add('preferences_unreadable')
    changed_preferences = [key for key in PREFERENCE_KEYS
                           if start_preferences['values'][key] != end_preferences['values'][key]]
    if changed_preferences:
        issues.add('preferences_changed')
    if start_mode != end_mode:
        issues.add('display_mode_changed')
    if start_interval != end_interval:
        issues.add('refresh_interval_changed')
    if any(sample['unreadable_current_pids'] for sample in samples):
        issues.add('some_observed_processes_unreadable')
    hosts = [sample['host_mib'] for sample in samples if sample['host_mib'] is not None]
    idle = [sample['host_mib'] for sample in samples
            if sample['host_mib'] is not None and not sample['related_pids']
            and not sample['unreadable_current_pids']]
    host_only_median = round(statistics.median(idle), 2) if idle else None
    result = {
        'valid': not issues,
        'schema_version': 2,
        'scenario': scenario,
        'source_revision': source_revision,
        'source_revision_provenance': 'caller supplied, not inferred or verified' if source_revision else 'not supplied',
        'build_start': initial_build, 'build_end': final_build,
        'system': environment,
        'preferences_start': start_preferences, 'preferences_end': end_preferences,
        'changed_preference_keys': changed_preferences,
        'inventory_method': 'libproc proc_listallpids + proc_pidinfo(PROC_PIDTBSDINFO) + proc_pidpath',
        'actual_sample_intervals_seconds': interval_statistics([b['seconds'] - a['seconds'] for a, b in zip(samples, samples[1:])]),
        'inventory_duration_seconds': interval_statistics([sample['inventory_seconds'] for sample in samples]),
        'invalid_reasons': sorted(issues),
        'recorded_at': datetime.now(timezone.utc).isoformat(),
        'app_path': str(app),
        'gui_processes_start': initial_guis,
        'gui_processes_end': final_guis,
        'observed_gui_roles': observed_roles,
        'mode_start': start_mode, 'mode_end': end_mode,
        'refresh_seconds_start': start_interval, 'refresh_seconds_end': end_interval,
        'requested_duration_seconds': duration,
        'sample_span_seconds': samples[-1]['seconds'] - samples[0]['seconds'],
        'requested_interval_seconds': interval,
        'sample_count': len(samples),
        'host_mib_median': round(statistics.median(hosts), 2) if hosts else None,
        'host_only_mib_median': host_only_median,
        'idle_host_mib_median': host_only_median,
        'deprecated_fields': {'idle_host_mib_median': 'Use host_only_mib_median; absence of related processes does not prove idle.'},
        'combined_mib_median': round(statistics.median(s['combined_mib'] for s in samples), 2),
        'observed_combined_peak_mib': round(max(s['combined_mib'] for s in samples), 2),
        'max_related_processes': max(len(s['related_pids']) for s in samples),
        'observed_related_pids': sorted({pid for s in samples for pid in s['related_pids']}),
        'sampled_host_only_percent': round(len(idle) / len(samples) * 100, 1),
        'memory_metric': 'proc_pid_rusage RUSAGE_INFO_V2 ri_phys_footprint / 2^20',
        'cpu_reported': False,
        'limitations': [
            'Sampled observations can miss short-lived processes, PID changes and peaks between samples.',
            'Theme, visibility, filters and refresh preferences are compared only at the beginning and end; transient UI changes may be missed.',
            'Host-only samples do not prove that the host was idle or its floating panel collapsed.',
            'Unavailable BSD info can hide new process ancestry; per-sample inventory diagnostics report read failures.',
            'Process discovery and rusage reads are not atomic; unreadable observed processes invalidate the run.',
            'The profiler itself and operating-system shared services are excluded.',
            'CPU and energy are omitted; no conclusions about responsiveness follow from memory alone.',
            'Display scaling, actual expanded state, workload and warm-up history must be controlled and described in scenario.',
        ],
    }
    return result, samples


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, default=Path.home() / 'Applications/Codex用量.app')
    parser.add_argument('--duration', type=float, default=130)
    parser.add_argument('--interval', type=float, default=0.2)
    parser.add_argument('--output', type=Path, default=ROOT / '.local/performance', help='Output directory')
    parser.add_argument('--scenario', help='Description of the controlled UI/workload state; recorded without verification')
    parser.add_argument('--source-revision', help='Optional source revision label; recorded as supplied, never guessed')
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('Physical-footprint profiling requires macOS.')
    if not 0 < args.duration <= 3600 or not 0.02 <= args.interval <= 60:
        parser.error('Duration must be 0–3600 seconds; interval must be 0.02–60 seconds.')
    try:
        result, samples = measure(args.app.expanduser().resolve(), args.duration, args.interval, args.scenario, args.source_revision)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        parser.exit(1, f'{error}\n')
    output = args.output.expanduser().resolve()
    output.mkdir(parents=True, exist_ok=True, mode=0o700)
    prefix = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + uuid.uuid4().hex[:8]
    samples_path = output / f'{prefix}-samples.json'
    result_path = output / f'{prefix}-performance.json'
    result['samples_file'] = samples_path.name
    save_json(samples_path, samples)
    save_json(result_path, result)
    print(json.dumps(dict(result, result_file=str(result_path)), ensure_ascii=False, indent=2))
    return 0 if result['valid'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
