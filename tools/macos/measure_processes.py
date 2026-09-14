#!/usr/bin/env python3
"""Sample an already-running macOS app and its related processes without UI actions.

Reports observed physical-footprint samples, not a guaranteed lifetime peak.
CPU and I/O deltas cover only the observed lifetime of each process identity.
"""
import argparse
import ctypes
from datetime import datetime, timezone
import json
from pathlib import Path
import statistics
import subprocess
import sys
import time
import uuid

from tools.common.paths import ROOT, macos_source
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


class Timebase(ctypes.Structure):
    _fields_ = [('numer', ctypes.c_uint32), ('denom', ctypes.c_uint32)]


class FDInfo(ctypes.Structure):
    _fields_ = [('fd', ctypes.c_int32), ('type', ctypes.c_uint32)]


COUNTERS = ('user_time', 'system_time', 'pkg_idle_wkups', 'interrupt_wkups',
            'diskio_bytesread', 'diskio_byteswritten')


def mach_timebase():
    library = ctypes.CDLL('/usr/lib/libSystem.B.dylib')
    library.mach_timebase_info.argtypes = [ctypes.POINTER(Timebase)]
    library.mach_timebase_info.restype = ctypes.c_int
    value = Timebase()
    if library.mach_timebase_info(ctypes.byref(value)) != 0 or not value.denom:
        raise RuntimeError('Mach timebase is unavailable; CPU conversion is unsafe.')
    return value.numer, value.denom


def read_fd_count(library, pid):
    library.proc_pidinfo.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint64,
                                    ctypes.c_void_p, ctypes.c_int]
    library.proc_pidinfo.restype = ctypes.c_int
    size = library.proc_pidinfo(pid, 1, 0, None, 0)  # PROC_PIDLISTFDS
    if size <= 0:
        return None
    for _ in range(4):
        size += 32 * ctypes.sizeof(FDInfo)
        if size > 16 * MIB:
            return None
        buffer = ctypes.create_string_buffer(size)
        used = library.proc_pidinfo(pid, 1, 0, buffer, size)
        if used <= 0 or used % ctypes.sizeof(FDInfo):
            return None
        if used < size:
            return used // ctypes.sizeof(FDInfo)
        size *= 2
    return None


def counter_summary(samples, timebase):
    # Never merge a recycled PID, or add ri_child_* to separately observed
    # descendants. First-to-last deltas exclude work before first observation.
    histories = {}
    for sample in samples:
        for pid, value in sample['process_counters'].items():
            histories.setdefault((pid, value['start_abstime']), []).append(value)
    rows = []
    for (pid, identity), values in histories.items():
        valid = all(b[key] >= a[key] for a, b in zip(values, values[1:]) for key in COUNTERS)
        delta = {key: values[-1][key] - values[0][key] for key in COUNTERS} if valid else None
        rows.append({'pid': pid, 'start_abstime': identity, 'observations': len(values),
                     'valid': valid, 'delta': delta,
                     'cpu_seconds': ((delta['user_time'] + delta['system_time']) * timebase[0]
                                     / timebase[1] / 1e9) if valid else None})
    return rows


def process_table():
    # comm exposes executable paths, never command-line arguments or credentials.
    output = subprocess.check_output(
        ['/bin/ps', '-axo', 'pid=,ppid=,comm='], text=True, timeout=5)
    table = {}
    for line in output.splitlines():
        fields = line.strip().split(None, 2)
        if len(fields) == 3:
            table[int(fields[0])] = (int(fields[1]), fields[2])
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


def preference(key):
    result = subprocess.run(
        ['/usr/bin/defaults', 'read', 'local.codex-usage.desktop', key],
        text=True, capture_output=True, timeout=5)
    return result.stdout.strip() if result.returncode == 0 else None


def read_usage(library, pid):
    value = Usage()
    return value if library.proc_pid_rusage(pid, 2, ctypes.byref(value)) == 0 else None


def save_json(path, value):
    with path.open('x', encoding='utf-8') as output:
        path.chmod(0o600)
        json.dump(value, output, ensure_ascii=False, indent=2)
        output.write('\n')


def measure(app, duration, interval):
    library = ctypes.CDLL('/usr/lib/libproc.dylib', use_errno=True)
    library.proc_pid_rusage.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
    library.proc_pid_rusage.restype = ctypes.c_int
    timebase = mach_timebase()
    host_path = str(app / 'Contents/MacOS/CodexUsage')
    main_path = str(app / 'Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage')
    gui_paths = {host_path: 'host', main_path: 'main'}

    def gui_pids(table):
        return {pid: gui_paths[command] for pid, (_, command) in table.items()
                if command in gui_paths}

    initial_table = process_table()
    initial_guis = gui_pids(initial_table)
    if sum(role == 'host' for role in initial_guis.values()) != 1:
        raise RuntimeError('Exactly one host must already be running from --app; no app was launched.')
    start_mode = preference('displayMode')
    start_interval = preference('refreshSeconds')
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
        table = process_table()
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
        counters = {}
        fds = {}
        unreadable = []
        for pid in sorted(current | known):
            usage = read_usage(library, pid)
            if usage is None:
                if pid in current:
                    unreadable.append(pid)
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
            counters[str(pid)] = dict(start_abstime=identity, **{key: getattr(usage, key) for key in COUNTERS})
            fds[str(pid)] = read_fd_count(library, pid)
        if any(pid not in measured for pid in initial_guis):
            issues.add('initial_gui_unreadable_or_exited')
        host_pids = [pid for pid, role in guis.items() if role == 'host']
        host = measured.get(host_pids[0]) if len(host_pids) == 1 else None
        related = sorted(pid for pid in measured if pid not in host_pids)
        samples.append({
            'seconds': round(time.monotonic() - began, 4),
            'gui_processes': guis,
            'host_mib': host / MIB if host is not None else None,
            'combined_mib': sum(measured.values()) / MIB,
            'process_mib': {str(pid): value / MIB for pid, value in measured.items()},
            'related_pids': related,
            'unreadable_current_pids': unreadable,
            'process_counters': counters,
            'process_fd_count': fds,
        })
        if time.monotonic() - began >= duration:
            break
        time.sleep(min(interval, max(0, duration - (time.monotonic() - began))))
    final_guis = gui_pids(process_table())
    if final_guis != initial_guis:
        issues.add('gui_process_set_changed')
    end_mode = preference('displayMode')
    end_interval = preference('refreshSeconds')
    if start_mode != end_mode:
        issues.add('display_mode_changed')
    if start_interval != end_interval:
        issues.add('refresh_interval_changed')
    if any(sample['unreadable_current_pids'] for sample in samples):
        issues.add('some_observed_processes_unreadable')
    hosts = [sample['host_mib'] for sample in samples if sample['host_mib'] is not None]
    idle = [sample['host_mib'] for sample in samples
            if sample['host_mib'] is not None and not sample['related_pids']]
    counter_rows = counter_summary(samples, timebase)
    if any(not row['valid'] for row in counter_rows):
        issues.add('resource_counter_regressed')
    span = samples[-1]['seconds'] - samples[0]['seconds']
    host_ids = {str(pid) for pid, role in initial_guis.items() if role == 'host'}
    host_cpu = sum(row['cpu_seconds'] or 0 for row in counter_rows if row['pid'] in host_ids)
    counter_valid = all(row['valid'] for row in counter_rows)
    result = {
        'valid': not issues,
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
        'idle_host_mib_median': round(statistics.median(idle), 2) if idle else None,
        'combined_mib_median': round(statistics.median(s['combined_mib'] for s in samples), 2),
        'observed_combined_peak_mib': round(max(s['combined_mib'] for s in samples), 2),
        'max_related_processes': max(len(s['related_pids']) for s in samples),
        'observed_related_pids': sorted({pid for s in samples for pid in s['related_pids']}),
        'sampled_host_only_percent': round(len(idle) / len(samples) * 100, 1),
        'memory_metric': 'proc_pid_rusage RUSAGE_INFO_V2 ri_phys_footprint / 2^20',
        'cpu_reported': counter_valid,
        'mach_timebase_numer_denom': list(timebase),
        'observed_process_counter_deltas': counter_rows,
        'host_cpu_seconds': host_cpu if counter_valid else None,
        'host_cpu_percent_one_core': host_cpu / span * 100 if counter_valid and span > 0 else None,
        'observed_combined_cpu_seconds': sum(row['cpu_seconds'] for row in counter_rows) if counter_valid else None,
        'fd_samples_complete': all(value is not None for sample in samples for value in sample['process_fd_count'].values()),
        'observed_fd_peak': max((sum(sample['process_fd_count'].values()) for sample in samples
                                 if all(value is not None for value in sample['process_fd_count'].values())), default=None),
        'limitations': [
            'Sampled observations can miss short-lived processes, PID changes and peaks between samples.',
            'Mode and refresh preferences are compared only at the beginning and end.',
            'Process discovery and rusage reads are not atomic; unreadable observed processes invalidate the run.',
            'The profiler itself and operating-system shared services are excluded.',
            'CPU uses Mach timebase conversion; 100% means one fully occupied CPU core.',
            'Counter deltas omit work before first and after last observation, including missed short-lived processes.',
            'Wakeup counters are kernel task-attributed events, not total machine wakeups or an energy measurement.',
            'Disk I/O counters do not include cached logical reads; zero may also mean unavailable kernel I/O accounting.',
            'FD counts are non-atomic observations and exclude Mach ports; unreadable FD lists are null, not zero.',
        ],
    }
    return result, samples


def main(argv=None, *, legacy=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, default=Path.home() / 'Applications/Codex用量.app')
    parser.add_argument('--duration', type=float, default=40)
    parser.add_argument('--interval', type=float, default=0.1)
    parser.add_argument('--output', type=Path, default=ROOT / ('.local/performance' if legacy else 'build/reports/macos'), help='Output directory')
    args = parser.parse_args(argv)
    if sys.platform != 'darwin':
        parser.error('Physical-footprint profiling requires macOS.')
    if not 0 < args.duration <= 3600 or not 0.02 <= args.interval <= 60:
        parser.error('Duration must be 0–3600 seconds; interval must be 0.02–60 seconds.')
    try:
        result, samples = measure(args.app.expanduser().resolve(), args.duration, args.interval)
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
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
