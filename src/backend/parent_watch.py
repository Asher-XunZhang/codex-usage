"""Wait for the original parent and local cancellation using native events."""
import errno
from contextlib import closing
import os
import select
import sys
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


class _MacOSStopEvent:
    """One-way Event with a readable pipe for process and HTTP waits.

    The byte stays unread so every waiter observes cancellation. The context
    owner closes the pipe only after the HTTP loop and process waiter finish.
    """
    def __init__(self, event):
        self.event = event
        self.lock = threading.RLock()
        self.read_fd, self.write_fd = os.pipe()
        self.notified = False
        self.finalizer = weakref.finalize(self, self._close_pipe, self.read_fd, self.write_fd)
        self.finalizer.atexit = False
        try:
            os.set_blocking(self.read_fd, False)
            os.set_blocking(self.write_fd, False)
            if event.is_set():
                self.set()
        except BaseException:
            self.close()
            raise

    @staticmethod
    def _close_pipe(read_fd, write_fd):
        try:
            os.close(read_fd)
        finally:
            os.close(write_fd)

    def fileno(self):
        return self.read_fd

    def is_set(self):
        return self.event.is_set()

    def wait(self, timeout=None):
        return self.event.wait(timeout)

    def set(self):
        with self.lock:
            self.event.set()
            if self.write_fd is not None and not self.notified:
                try:
                    os.write(self.write_fd, b'\0')
                except BlockingIOError:
                    pass  # A full pipe already signals cancellation.
                self.notified = True

    def close(self):
        with self.lock:
            if self.write_fd is not None:
                self.finalizer()
                self.read_fd = self.write_fd = None


class ParentMonitor:
    """Own native cancellation resources and a waiter for one worker lifetime.

    Assign monitor.stop before installing signal handlers or starting workers.
    macOS also needs the pipe without a parent, for signal-driven HTTP shutdown.
    Other Unix platforms keep the original Event and polling fallback.
    """
    def __init__(self, parent_pid, stop, *, compact=False):
        self.windows = os.name == 'nt' and parent_pid is not None
        self.native = self.windows or sys.platform == 'darwin'
        self.stop = (_WindowsStopEvent(stop) if self.windows else
                     _MacOSStopEvent(stop) if sys.platform == 'darwin' else stop)
        self.target = watch_compact_parent if compact else watch_parent
        self.parent_pid = parent_pid
        self.thread = (threading.Thread(target=self._run, daemon=True)
                       if parent_pid is not None else None)
        self.start_attempted = False

    def _run(self):
        try:
            self.target(self.parent_pid, self.stop)
        finally:
            if self.windows:
                self.stop.close()

    def __enter__(self):
        return self

    def start(self):
        if self.thread is not None:
            self.start_attempted = True
            self.thread.start()

    def __exit__(self, *_):
        if self.native:
            joined = False
            try:
                self.stop.set()
                if self.thread is not None and self.thread.ident is not None:
                    self.thread.join()
                    joined = True
            finally:
                # Never close a handle while WaitForMultipleObjects uses it.
                # An interrupted start can have created a thread not yet given
                # an ident. That thread closes its own handle; if creation
                # failed entirely, finalization reclaims the unborrowed handle.
                # macOS shares its pipe with the HTTP loop, so the waiter must
                # not close it. An interrupted start with no ident leaves GC
                # ownership until the thread (if created) releases the monitor.
                if (not self.start_attempted or
                        (not self.windows and (self.thread is None or joined))):
                    self.stop.close()


def _watch_macos_parent(parent_pid, stop):
    if stop.is_set():
        return
    try:
        if os.getppid() != parent_pid:
            stop.set()
            return
        with closing(select.kqueue()) as queue:
            queue.control([
                select.kevent(parent_pid, filter=select.KQ_FILTER_PROC,
                              flags=select.KQ_EV_ADD | select.KQ_EV_ONESHOT,
                              fflags=select.KQ_NOTE_EXIT),
                select.kevent(stop.fileno(), filter=select.KQ_FILTER_READ,
                              flags=select.KQ_EV_ADD | select.KQ_EV_ONESHOT),
            ], 0, 0)
            # Close the check/register race, including reuse of the old PID.
            if os.getppid() != parent_pid:
                stop.set()
                return
            while not stop.is_set():
                for event in queue.control(None, 2, None):
                    if event.flags & select.KQ_EV_ERROR:
                        raise OSError(event.data, os.strerror(event.data))
                    if (event.filter == select.KQ_FILTER_READ or
                            (event.filter == select.KQ_FILTER_PROC and event.fflags & select.KQ_NOTE_EXIT)):
                        stop.set()
                        return
    except OSError as error:
        stop.set()
        if error.errno != errno.ESRCH:  # Parent can exit while registering.
            raise
    except BaseException:
        stop.set()
        raise


def watch_parent(parent_pid, stop):
    """Watch the dashboard worker; polling is only a non-native fallback."""
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
    elif isinstance(stop, _MacOSStopEvent):
        _watch_macos_parent(parent_pid, stop)
    else:
        while not stop.is_set():
            if os.getppid() != parent_pid:
                stop.set()
                return
            stop.wait(0.5)


def watch_compact_parent(parent_pid, stop):
    """Bounded scans share native waits; other Unix keeps its short fallback."""
    if os.name == 'nt' or isinstance(stop, _MacOSStopEvent):
        watch_parent(parent_pid, stop)
        return
    while not stop.wait(0.2):
        if os.getppid() != parent_pid:
            stop.set()
            return
