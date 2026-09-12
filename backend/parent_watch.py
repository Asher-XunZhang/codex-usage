"""Wait for the original parent's identity, including Windows handle semantics."""
import os
import threading
import weakref


class _WindowsStopEvent:
    """One-way stop shared by Python waiters and a native process wait."""
    def __init__(self, event):
        import ctypes
        from ctypes import wintypes
        self.event = event
        self.lock = threading.RLock()
        self.kernel = kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
        kernel.CreateEventW.restype = wintypes.HANDLE
        kernel.SetEvent.argtypes = [wintypes.HANDLE]
        kernel.SetEvent.restype = wintypes.BOOL
        kernel.WaitForMultipleObjects.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE), wintypes.BOOL, wintypes.DWORD]
        kernel.WaitForMultipleObjects.restype = wintypes.DWORD
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        self.handle = kernel.CreateEventW(None, True, event.is_set(), None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        self.finalizer = weakref.finalize(self, kernel.CloseHandle, self.handle)
        # At interpreter exit a daemon may still be inside the native wait.
        # Only normal ownership/GC closes here; process teardown belongs to OS.
        self.finalizer.atexit = False

    def is_set(self):
        return self.event.is_set()

    def wait(self, timeout=None):
        return self.event.wait(timeout)

    def set(self):
        import ctypes
        with self.lock:
            self.event.set()
            # Request threads may finish after the monitor owner has closed.
            if self.handle and not self.kernel.SetEvent(self.handle):
                raise ctypes.WinError(ctypes.get_last_error())

    def close(self):
        # The owner must join the native waiter before closing its event.
        with self.lock:
            if self.handle:
                self.finalizer()
                self.handle = None


class ParentMonitor:
    """Own the Windows stop handle and waiter for one worker lifetime.

    Assign the yielded monitor.stop before installing signal handlers or starting
    worker threads, then call start(). Unix callers keep their original Event.
    """
    def __init__(self, parent_pid, stop, *, compact=False):
        self.native = os.name == 'nt' and parent_pid is not None
        self.stop = _WindowsStopEvent(stop) if self.native else stop
        self.target = watch_compact_parent if compact else watch_parent
        self.parent_pid = parent_pid
        self.thread = (threading.Thread(target=self._run, daemon=True)
                       if parent_pid is not None else None)
        self.start_attempted = False

    def _run(self):
        try:
            self.target(self.parent_pid, self.stop)
        finally:
            if self.native:
                self.stop.close()

    def __enter__(self):
        return self

    def start(self):
        if self.thread is not None:
            self.start_attempted = True
            self.thread.start()

    def __exit__(self, *_):
        if self.native:
            try:
                self.stop.set()
                if self.thread.ident is not None:
                    self.thread.join()
            finally:
                # Never close a handle while WaitForMultipleObjects uses it.
                # An interrupted start can have created a thread not yet given
                # an ident. That thread closes its own handle; if creation
                # failed entirely, finalization reclaims the unborrowed handle.
                if not self.start_attempted:
                    self.stop.close()


def watch_parent(parent_pid, stop):
    """Watch the dashboard worker; preserve its Unix half-second cadence."""
    if os.name == 'nt':
        import ctypes
        from ctypes import wintypes
        kernel = stop.kernel
        handle = None
        try:
            handle = kernel.OpenProcess(0x00100000, False, parent_pid)
            if not handle or os.getppid() != parent_pid:
                stop.set()
                return
            handles = (wintypes.HANDLE * 2)(handle, stop.handle)
            result = kernel.WaitForMultipleObjects(2, handles, False, 0xFFFFFFFF)
            if result == 0:  # Parent exited; no timeout or periodic polling.
                stop.set()
            elif result != 1:  # Local stop is the only other expected result.
                error = ctypes.get_last_error()
                stop.set()
                if result == 0xFFFFFFFF:
                    raise ctypes.WinError(error)
                raise OSError(f'Unexpected parent process wait result: {result}')
        except BaseException:
            stop.set()
            raise
        finally:
            if handle:
                kernel.CloseHandle(handle)
    else:
        while not stop.is_set():
            if os.getppid() != parent_pid:
                stop.set()
                return
            stop.wait(0.5)


def watch_compact_parent(parent_pid, stop):
    """Unix compact scans wait before polling; Windows retains handle ownership."""
    if os.name == 'nt':
        watch_parent(parent_pid, stop)
        return
    while not stop.wait(0.2):
        if os.getppid() != parent_pid:
            stop.set()
            return
