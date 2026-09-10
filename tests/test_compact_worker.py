import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import tracemalloc
import unittest

from disk_index import DiskUsageIndex
from test_token_usage import meta, start, record, u, event


class CompactWorkerTests(unittest.TestCase):
    def test_collects_from_unicode_home_then_exits_and_reuses_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory); home=root/'本机 数据'; logs=home/'sessions'; logs.mkdir(parents=True)
            events=[meta('task'),*start('turn'),record('task','turn','r1')]
            log=logs/'one.jsonl'; log.write_text(''.join(json.dumps(e)+'\n' for e in events))
            output=root/'result.json'; cache=root/'index.sqlite'
            command=[sys.executable,'-E','-s','-B',str(Path(__file__).parents[1]/'backend/compact_snapshot.py'),
                '--codex-home',str(home),'--cache-path',str(cache),'--output',str(output),
                '--parent-pid',str(os.getpid()),'--days','all','--refresh-seconds','0']
            first=subprocess.run(command,timeout=5,capture_output=True)
            self.assertEqual(first.returncode,0,first.stderr.decode())
            snapshot=json.loads(output.read_text())
            self.assertEqual(snapshot['filtered']['summary']['total_tokens'],120)
            self.assertNotIn('groups',snapshot['filtered'])
            with log.open('a') as stream: stream.write(json.dumps(record('task','turn','r2',value=u(200,40,80,10)))+'\n')
            second=subprocess.run(command,timeout=5,capture_output=True)
            self.assertEqual(second.returncode,0,second.stderr.decode())
            self.assertEqual(json.loads(output.read_text())['filtered']['summary']['total_tokens'],360)
            self.assertFalse(output.with_suffix('.tmp').exists())

    def test_history_size_does_not_create_resident_python_row_objects(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory); (root/'sessions').mkdir()
            with (root/'sessions/history.jsonl').open('w') as stream:
                for item in [meta('task'),*start('turn'),event('response_item',{'content':'SECRET_MESSAGE_BODY'})]:
                    stream.write(json.dumps(item)+'\n')
                for i in range(10000): stream.write(json.dumps(record('task','turn',f'r{i}'))+'\n')
            index=DiskUsageIndex(root,root/'index.sqlite')
            tracemalloc.start()
            try:
                index.scan(); result=index.query(days='all')
                _,peak=tracemalloc.get_traced_memory()
            finally: tracemalloc.stop()
            self.assertEqual(result['summary']['requests'],10000)
            self.assertLess(peak,8*1024*1024,'Accounting history must stay on disk, not in Python collections')
            self.assertNotIn(b'SECRET_MESSAGE_BODY',(root/'index.sqlite').read_bytes())


if __name__=='__main__': unittest.main()
