"""Preserve the distinct macOS worker lifetimes when sharing Windows support."""
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import parent_watch


class ParentWatchTests(unittest.TestCase):
    def run_watch(self, function, *, parent_alive=True, cancel_on_wait=2, stopped=False):
        events = []

        class Stop:
            waits = 0
            done = stopped

            def is_set(self):
                return self.done

            def set(self):
                events.append('stop')
                self.done = True

            def wait(self, seconds):
                events.append(('wait', seconds))
                self.waits += 1
                self.done = self.done or self.waits >= cancel_on_wait
                return self.done

        def getppid():
            events.append('check')
            return 42 if parent_alive else 1

        stop = Stop()
        with patch.object(parent_watch, 'os', SimpleNamespace(name='posix', getppid=getppid)):
            function(42, stop)
        return events, stop.done

    def test_dashboard_checks_immediately_then_every_half_second(self):
        events, _ = self.run_watch(parent_watch.watch_parent)
        self.assertEqual(events, ['check', ('wait', .5), 'check', ('wait', .5)])

    def test_dashboard_stops_immediately_when_parent_is_gone(self):
        events, stopped = self.run_watch(parent_watch.watch_parent, parent_alive=False)
        self.assertEqual(events, ['check', 'stop'])
        self.assertTrue(stopped)

    def test_dashboard_does_not_poll_after_collection_has_stopped(self):
        events, _ = self.run_watch(parent_watch.watch_parent, stopped=True)
        self.assertEqual(events, [])

    def test_compact_waits_before_first_check_and_keeps_short_interval(self):
        events, _ = self.run_watch(parent_watch.watch_compact_parent)
        self.assertEqual(events, [('wait', .2), 'check', ('wait', .2)])

    def test_compact_cancel_during_first_wait_does_not_poll_parent(self):
        events, stopped = self.run_watch(parent_watch.watch_compact_parent, cancel_on_wait=1)
        self.assertEqual(events, [('wait', .2)])
        self.assertTrue(stopped)

    def test_compact_parent_exit_stops_collection_after_initial_wait(self):
        events, stopped = self.run_watch(parent_watch.watch_compact_parent, parent_alive=False)
        self.assertEqual(events, [('wait', .2), 'check', 'stop'])
        self.assertTrue(stopped)


if __name__ == '__main__':
    unittest.main()
