import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from disk_index import DiskUsageIndex
from dashboard_data import UsageIndex
import test_dashboard
from test_token_usage import meta, record, start, u


class DiskDashboardTests(test_dashboard.DashboardTests):
    """Run the existing accounting/filter/export regressions on the production store."""
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.home = Path(self.tmp.name)
        self.cache = self.home/'cache/index.sqlite'
        self.index = DiskUsageIndex(self.home,self.cache)

    def test_reopen_reuses_persisted_cursor_without_reading_old_log(self):
        self.write('root',[meta('root'),*start('t1'),record('root','t1','r1')])
        self.index.scan()
        expected=self.query()['summary']
        self.index=DiskUsageIndex(self.home,self.cache)
        with patch('disk_index.DiskLogFile',side_effect=AssertionError('unchanged log must not be replayed')):
            self.index.scan()
        self.assertEqual(self.query()['summary'],expected)

    def test_reference_and_disk_queries_agree_across_filters(self):
        self.write('root',[meta('root'),*start('t1'),record('root','t1','r1')])
        self.write('child',[meta('child',parent='root'),*start('t2'),record('child','t2','r2',value=u(200,40,120,10))])
        self.write('old',[meta('old'),*start('t3'),self.old_call(u())])
        reference=UsageIndex(self.home); reference.scan(); self.index.scan()
        from test_dashboard import NOW
        for days in ('1','7','30','90','all'):
            for group in ('model','task'):
                for compact in (False,True):
                    for task in ('all','root','old'):
                        filters=dict(days=days,group=group,summary_only=compact,task=task,now=NOW)
                        a=reference.query(**filters); b=self.index.query(**filters)
                        a['meta'].pop('generated_at'); b['meta'].pop('generated_at')
                        self.assertEqual(a,b,filters)

    def test_deleted_log_removed_after_restart(self):
        path=self.write('root',[meta('root'),*start('t1'),record('root','t1','r1')])
        self.index.scan(); path.unlink()
        self.index=DiskUsageIndex(self.home,self.cache); self.index.scan()
        self.assertIsNone(self.query()['summary']['total_tokens'])

    def test_cache_cannot_be_reused_for_a_different_home(self):
        with self.assertRaises(ValueError):
            DiskUsageIndex(self.home/'different',self.cache)


if __name__=='__main__': unittest.main()
