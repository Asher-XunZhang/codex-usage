"""Opt-in, bounded desktop stdout events; default HTTP protocol is unchanged."""
from __future__ import annotations
import json
import os
import threading


class DesktopEvents:
    def __init__(self, descriptor, instance_id):
        self.descriptor = descriptor
        self.instance_id = instance_id
        self.condition = threading.Condition()
        self.pending = {}
        self.closed = False
        self.thread = threading.Thread(target=self._write, name='desktop-events', daemon=True)
        self.thread.start()

    def publish(self, state, *, ready=False):
        # One pending handshake and one latest state. A slow UI cannot retain an
        # unbounded queue or block the indexer while it holds a data lock.
        event = 'desktop_ready' if ready else 'desktop_state'
        allowed = ('url', 'status', 'ready', 'generated_at', 'scanning', 'refresh_seconds',
                   'refresh_requested', 'refresh_completed', 'refresh_error', 'scan_duration_ms')
        value = {key: state[key] for key in allowed if key in state}
        if isinstance(value.get('refresh_error'), str):
            value['refresh_error'] = value['refresh_error'][:512]
        value.update(event=event, protocol=1, pid=os.getpid(), instance_id=self.instance_id)
        data = json.dumps(value, ensure_ascii=True, separators=(',', ':')).encode() + b'\n'
        if len(data) > 4096:
            return
        with self.condition:
            if not self.closed:
                self.pending[event] = data
                self.condition.notify()

    def _write(self):
        while True:
            with self.condition:
                self.condition.wait_for(lambda: self.closed or self.pending)
                if self.closed:
                    return
                key = 'desktop_ready' if 'desktop_ready' in self.pending else 'desktop_state'
                data = self.pending.pop(key)
            try:
                view = memoryview(data)
                while view:
                    view = view[os.write(self.descriptor, view):]
            except (OSError, ValueError):
                self.close()
                return

    def close(self):
        with self.condition:
            self.closed = True
            self.pending.clear()
            self.condition.notify_all()
