"""One bounded collection for the menu/capsule; exits immediately after publication."""
import argparse
import json
import os
from pathlib import Path
import signal
import threading
import time
from disk_index import DiskUsageIndex


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--codex-home',type=Path,required=True)
    parser.add_argument('--cache-path',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--parent-pid',type=int,required=True)
    parser.add_argument('--days',default='30')
    parser.add_argument('--model',default='all')
    parser.add_argument('--task',default='all')
    parser.add_argument('--refresh-seconds',type=int,default=10)
    args=parser.parse_args()
    if args.parent_pid<=1 or os.getppid()!=args.parent_pid:
        parser.error('A live direct parent is required')
    index=DiskUsageIndex(args.codex_home,args.cache_path,args.refresh_seconds)
    for sig in (signal.SIGTERM,signal.SIGINT): signal.signal(sig,lambda *_:index.stop.set())
    def watch():
        while not index.stop.wait(.2):
            if os.getppid()!=args.parent_pid:
                index.stop.set(); return
    threading.Thread(target=watch,daemon=True).start()
    started=time.monotonic()
    try:
        index.scan()
        if index.stop.is_set(): return
        today=index.query(days='1',summary_only=True)
        reset=False
        try: filtered=index.query(days=args.days,model=args.model,task=args.task,summary_only=True)
        except ValueError:
            filtered=index.query(days=args.days,summary_only=True); reset=True
        output=dict(today=today,filtered=filtered,filter_reset=reset,scan_ms=round((time.monotonic()-started)*1000))
        encoded=json.dumps(output,ensure_ascii=False).encode()
        if len(encoded)>65536: raise ValueError('Unexpected snapshot size')
        args.output.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
        temporary=args.output.with_suffix('.tmp')
        with temporary.open('wb') as stream:
            os.chmod(temporary,0o600); stream.write(encoded)
        temporary.replace(args.output)
    except InterruptedError:
        return


if __name__=='__main__': main()
