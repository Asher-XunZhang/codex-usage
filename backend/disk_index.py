"""Bounded-memory index: durable file cursors and SQL aggregates, no resident history.

The accounting parser is shared with the reference in-memory index. SQLite stores
only accounting fields and bounded naming metadata, never conversation bodies.
"""
from collections import defaultdict
from contextlib import closing
from datetime import datetime, timedelta
import json
from pathlib import Path
import sqlite3
import time

from dashboard_data import UsageIndex, LogFile, TZ
from token_usage import FIELDS, CORE, valid_id

COUNTERS = ','.join(FIELDS)
STATE_FIELDS = ('meta', 'offset', 'size', 'mtime', 'model', 'legacy', 'modern', 'issues',
                'pending_tail', 'legacy_previous', 'legacy_reset', 'legacy_skipped', 'turn', 'identity')


def sums():
    return ','.join(f'CASE WHEN COUNT({k})=COUNT(*) THEN COALESCE(SUM({k}),0) END AS {k}' for k in FIELDS)


def finish_total(row):
    result = dict(row)
    cached = result['cached_input_tokens']
    result['noncached_input_tokens'] = result['input_tokens'] - cached if cached is not None else None
    result['cache_hit_rate'] = cached / result['input_tokens'] * 100 if cached is not None and result['input_tokens'] else None
    return result


class RecordMap:
    def __init__(self, db, path, kind):
        self.db, self.path, self.kind = db, str(path), kind

    def get(self, key):
        found = self.db.execute('SELECT body FROM records WHERE path=? AND kind=? AND response=?',
                                (self.path, self.kind, key)).fetchone()
        return json.loads(found[0]) if found else None

    def __setitem__(self, key, row):
        hint = row['thread_total'] or {}
        values = (self.path, self.kind, key, row['thread'], row['turn'], row['model'], row['timestamp'], row['date'],
                  json.dumps(row, separators=(',', ':')), *(row['usage'][k] for k in FIELDS), *(hint.get(k) for k in CORE))
        self.db.execute('INSERT OR REPLACE INTO records VALUES (' + ','.join('?' for _ in values) + ')', values)

    def clear(self):
        self.db.execute('DELETE FROM records WHERE path=? AND kind=?', (self.path, self.kind))

    def append(self, row):
        self[row['response']] = row

    def __len__(self):
        return self.db.execute('SELECT COUNT(*) FROM records WHERE path=? AND kind=?', (self.path, self.kind)).fetchone()[0]


class DiskLogFile(LogFile):
    def __init__(self, path, db=None, state=None):
        # LogFile.update invokes __init__ on rotation/truncation. Keep that reset
        # behavior, including deleting rows associated with the old file identity.
        self.db = db if db is not None else self.db
        super().__init__(path)
        self.rows = RecordMap(self.db, path, 'modern')
        self.legacy_rows = RecordMap(self.db, path, 'legacy')
        if state is None:
            self.rows.clear(); self.legacy_rows.clear()
        else:
            for key in STATE_FIELDS:
                setattr(self, key, state[key])
            self.issues = set(self.issues)
            self.identity = tuple(self.identity) if self.identity is not None else None

    def state(self):
        return {key: sorted(self.issues) if key == 'issues' else getattr(self, key) for key in STATE_FIELDS}


class DiskUsageIndex(UsageIndex):
    def __init__(self, home, cache, refresh_seconds=30):
        super().__init__(home, refresh_seconds)
        self.cache = Path(cache)
        self.cache.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        with closing(self.connect()) as db:
            if 'body' in {r[1] for r in db.execute('PRAGMA table_info(usage)')}:
                db.executescript("DROP TABLE usage; DELETE FROM daily; DELETE FROM kv WHERE key='snapshot';")
            db.executescript('''
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY, state TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS records(
                    path TEXT, kind TEXT, response TEXT, thread TEXT, turn TEXT, model TEXT,
                    timestamp TEXT, date TEXT, body TEXT,
                    input_tokens INTEGER, output_tokens INTEGER, total_tokens INTEGER,
                    cached_input_tokens INTEGER, reasoning_output_tokens INTEGER, cache_write_input_tokens INTEGER,
                    hint_input INTEGER, hint_output INTEGER, hint_total INTEGER,
                    PRIMARY KEY(path,kind,response));
                CREATE INDEX IF NOT EXISTS records_identity ON records(kind,thread,response,path);
                CREATE INDEX IF NOT EXISTS records_kind_path ON records(kind,path);
                CREATE INDEX IF NOT EXISTS records_thread ON records(thread);
                CREATE TABLE IF NOT EXISTS ownership(thread TEXT PRIMARY KEY, task TEXT, legacy_ok INTEGER);
                CREATE TABLE IF NOT EXISTS usage AS SELECT thread AS task,thread,response,turn,model,timestamp,date,kind,
                    input_tokens,output_tokens,total_tokens,cached_input_tokens,reasoning_output_tokens,
                    cache_write_input_tokens,hint_input,hint_output,hint_total FROM records WHERE 0;
                CREATE INDEX IF NOT EXISTS usage_thread ON usage(thread,hint_total);
                CREATE INDEX IF NOT EXISTS usage_task ON usage(task);
                CREATE TABLE IF NOT EXISTS daily AS SELECT task,model,date,timestamp,
                    input_tokens,output_tokens,total_tokens,cached_input_tokens,reasoning_output_tokens,
                    cache_write_input_tokens,0 AS requests FROM usage WHERE 0;
                CREATE INDEX IF NOT EXISTS daily_range ON daily(date,model,task);
                CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS thread_totals(thread TEXT PRIMARY KEY,a INTEGER,b INTEGER,c INTEGER,
                    ha INTEGER,hb INTEGER,hc INTEGER);
            ''')
            version = db.execute("SELECT value FROM kv WHERE key='aggregate_version'").fetchone()
            if not version or version[0] != '3':
                db.execute("DELETE FROM kv WHERE key='snapshot'")
                db.execute("INSERT OR REPLACE INTO kv VALUES ('aggregate_version','3')")
            existing = db.execute("SELECT value FROM kv WHERE key='home'").fetchone()
            home_key = str(self.home.resolve())
            if existing and json.loads(existing[0]) != home_key:
                raise ValueError('Cache belongs to a different Codex home')
            db.execute("INSERT OR REPLACE INTO kv VALUES ('home',?)", (json.dumps(home_key),))
            db.commit()
        self.cache.chmod(0o600)

    def connect(self):
        db = sqlite3.connect(self.cache, timeout=10)
        db.row_factory = sqlite3.Row
        db.execute('PRAGMA cache_size=-1024')
        db.execute('PRAGMA mmap_size=0')
        db.execute('PRAGMA temp_store=FILE')
        return db

    def scan(self):
        paths = set()
        for directory in (self.home / 'sessions', self.home / 'archived_sessions'):
            if directory.is_dir():
                paths.update(directory.rglob('*.jsonl'))
        errors = set()
        if not paths:
            errors.add('未发现会话日志；请先在 Codex 完成一次对话，或检查 CODEX_HOME 和读取权限')
        with closing(self.connect()) as db, db:
            cached = {Path(r['path']): json.loads(r['state']) for r in db.execute('SELECT * FROM files')}
            changed = set(cached) != paths
            changed_threads = set()
            for old in set(cached) - paths:
                if cached[old]['meta']: changed_threads.add(cached[old]['meta']['id'])
                db.execute('DELETE FROM records WHERE path=?', (str(old),))
                db.execute('DELETE FROM files WHERE path=?', (str(old),))
                del cached[old]
            for path in sorted(paths):
                if self.stop.is_set():
                    raise InterruptedError('Index stopped')
                self.progress()
                state = cached.get(path)
                try:
                    stat = path.stat()
                    if state and state['meta'] is not None and (state['size'], state['mtime'], state['identity']) == (
                            stat.st_size, stat.st_mtime_ns, [stat.st_dev, stat.st_ino]):
                        continue
                    item = DiskLogFile(path, db, state)
                    item.update(self.progress, self.stop)
                    if state and state['meta']: changed_threads.add(state['meta']['id'])
                    if item.meta: changed_threads.add(item.meta['id'])
                    cached[path] = item.state()
                    db.execute('INSERT OR REPLACE INTO files VALUES (?,?)', (str(path), json.dumps(cached[path])))
                    changed = True
                except OSError:
                    errors.add('部分日志暂时无法读取')
                    changed = True
            signature = []
            for name in ('session_index.jsonl', 'state_5.sqlite', 'state_5.sqlite-wal'):
                try:
                    stat = (self.home / name).stat()
                    signature.append([name, stat.st_ino, stat.st_size, stat.st_mtime_ns])
                except OSError:
                    signature.append([name, None])
            previous = db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()
            snapshot = json.loads(previous[0]) if previous else None
            rebuild = changed or not snapshot or signature != snapshot.get('signature') or snapshot.get('retry')
            if rebuild:
                snapshot = self.rebuild(db, cached, errors, changed_threads if snapshot else None)
            snapshot['signature'] = signature
            snapshot['generated_at'] = datetime.now(TZ).isoformat()
            db.execute("INSERT OR REPLACE INTO kv VALUES ('snapshot',?)", (json.dumps(snapshot),))
        with self.lock:
            self.updated = snapshot['generated_at']
            self.scanned = len(paths)
            self.excluded = snapshot['excluded_legacy_threads']
            self.issues = snapshot['issues']
            self.coverage = snapshot['coverage']
            self.loading = False
            self.last_success_monotonic = time.monotonic()
        # Per-file state, parser objects and DB page cache are released each scan.

    def rebuild(self, db, cached, errors, changed_threads):
        registry, by_thread = {}, defaultdict(list)
        legacy, modern = set(), set()
        for path in sorted(cached):
            item = cached[path]
            errors.update(item['issues'])
            if item['pending_tail']:
                errors.add('正在写入的日志尾部将在后续刷新纳入')
            if not item['meta']:
                continue
            if not item['modern'] and not item['legacy']:
                errors.add('部分会话尚无可识别的记账记录，不能据此认定没有消耗')
            tid = item['meta']['id']
            registry[tid] = item['meta']
            by_thread[tid].append((path, item))
            if item['legacy']: legacy.add(tid)
            if item['modern']: modern.add(tid)
        recovered, partial, reasons = set(), set(), defaultdict(int)
        reconstructed = 0
        legacy_counts = dict(db.execute("SELECT path,COUNT(*) FROM records WHERE kind='legacy' GROUP BY path"))
        for tid in legacy - modern:
            items = by_thread[tid]
            path, item = items[0]
            count = legacy_counts.get(str(path), 0)
            if any(x['meta'].get('fork') or x['meta'].get('parent') for _, x in items):
                reasons['分叉或子代理归属不明'] += 1
            elif len(items) != 1:
                reasons['多个日志副本的先后关系不明'] += 1
            elif item['legacy_reset']:
                reasons['累计计数重置或差值无效'] += 1
            elif not count:
                reasons['缺少可交叉核对的调用记录'] += 1
            else:
                recovered.add(tid); reconstructed += count
                if item['legacy_skipped']: partial.add(tid)
        def root(tid):
            seen = set()
            while tid in registry and registry[tid].get('parent'):
                if tid in seen:
                    errors.add('存在异常的子代理关系'); break
                seen.add(tid); tid = registry[tid]['parent']
            return tid
        ownership = {t: (root(t), int(t in recovered)) for t in registry}
        previous_owners = {r[0]: (r[1], r[2]) for r in db.execute('SELECT * FROM ownership')}
        affected = set(registry) | set(previous_owners) if changed_threads is None else set(changed_threads)
        affected.update(t for t in set(ownership)|set(previous_owners) if ownership.get(t)!=previous_owners.get(t))
        db.execute('CREATE TEMP TABLE dirty_threads(thread TEXT PRIMARY KEY)')
        db.executemany('INSERT INTO dirty_threads VALUES (?)', ((t,) for t in affected))
        dirty_tasks = {owner[0] for t in affected for owner in (ownership.get(t),previous_owners.get(t)) if owner}
        db.execute('CREATE TEMP TABLE dirty_tasks(task TEXT PRIMARY KEY)')
        db.executemany('INSERT INTO dirty_tasks VALUES (?)', ((t,) for t in dirty_tasks))
        db.execute('DELETE FROM ownership')
        db.executemany('INSERT INTO ownership VALUES (?,?,?)', ((t, *owner) for t, owner in ownership.items()))
        # Conflicting duplicates keep their first file's record and surface coverage.
        conflict = db.execute("""WITH duplicates AS (
            SELECT thread,response FROM records WHERE kind='modern' GROUP BY thread,response HAVING COUNT(*)>1)
            SELECT 1 FROM records r JOIN duplicates d ON r.thread=d.thread AND r.response=d.response
            WHERE r.kind='modern' GROUP BY r.thread,r.response
            HAVING COUNT(DISTINCT json_extract(body,'$.usage') || COALESCE(turn,''))>1 LIMIT 1""").fetchone()
        if conflict:
            errors.add('存在冲突的重复记账，统计可能不完整')
        db.execute('DELETE FROM usage WHERE thread IN (SELECT thread FROM dirty_threads)')
        db.execute('''INSERT INTO usage SELECT o.task,r.thread,r.response,r.turn,r.model,r.timestamp,r.date,r.kind,
            r.input_tokens,r.output_tokens,r.total_tokens,r.cached_input_tokens,r.reasoning_output_tokens,
            r.cache_write_input_tokens,r.hint_input,r.hint_output,r.hint_total FROM records r JOIN ownership o ON o.thread=r.thread
            WHERE r.thread IN (SELECT thread FROM dirty_threads) AND ((r.kind='legacy' AND o.legacy_ok=1) OR (r.kind='modern' AND r.rowid=(
              SELECT r2.rowid FROM records r2 WHERE r2.kind='modern' AND r2.thread=r.thread AND r2.response=r.response
              ORDER BY r2.path LIMIT 1)))''')
        # Prefer a known model only for equivalent duplicate accounting.
        equivalents = ' AND '.join(f'r.{k} IS usage.{k}' for k in FIELDS)
        db.execute(f'''UPDATE usage SET model=COALESCE((SELECT r.model FROM records r WHERE r.kind='modern'
            AND r.thread=usage.thread AND r.response=usage.response AND r.model!='未标注模型'
            AND {equivalents} AND r.turn=usage.turn
            ORDER BY r.path LIMIT 1),model) WHERE kind='modern' AND model='未标注模型'
            AND thread IN (SELECT thread FROM dirty_threads) ''')
        names, archived, system_tasks = self.names(errors)
        gaps = []
        db.execute('DELETE FROM thread_totals WHERE thread IN (SELECT thread FROM dirty_threads)')
        db.execute('''INSERT INTO thread_totals SELECT thread,SUM(input_tokens) AS a,SUM(output_tokens) AS b,SUM(total_tokens) AS c,
                (SELECT hint_input FROM usage h WHERE h.thread=u.thread AND hint_total IS NOT NULL ORDER BY hint_total DESC LIMIT 1) AS ha,
                (SELECT hint_output FROM usage h WHERE h.thread=u.thread AND hint_total IS NOT NULL ORDER BY hint_total DESC LIMIT 1) AS hb,
                MAX(hint_total) AS hc FROM usage u WHERE thread IN (SELECT thread FROM dirty_threads) GROUP BY thread''')
        for r in db.execute('SELECT * FROM thread_totals WHERE hc IS NOT NULL'):
            if (r['a'],r['b'],r['c']) != (r['ha'],r['hb'],r['hc']):
                gaps.append(dict(thread=r['thread'],label=names.get(r['thread']) or '未命名任务',
                    recorded_total=r['c'],cumulative_total=r['hc'],difference=r['hc']-r['c']))
        if gaps:
            errors.add(f'{len(gaps)} 个任务存在累计差异，详见下方全局覆盖诊断；差额未重复加入用量')
        db.execute('DELETE FROM daily WHERE task IN (SELECT task FROM dirty_tasks)')
        db.execute(f'INSERT INTO daily SELECT task,model,date,MAX(timestamp),{sums()},COUNT(*) FROM usage WHERE task IN (SELECT task FROM dirty_tasks) GROUP BY task,model,date')
        tasks = []
        for row in db.execute('SELECT task,MAX(timestamp) AS last,COUNT(DISTINCT model) AS n,MIN(model) AS model FROM daily GROUP BY task ORDER BY task'):
            tid, last = row['task'], row['last']
            system = tid in system_tasks or (row['n']==1 and row['model']=='codex-auto-review')
            if system: names[tid] = f"后台审批检查 · {last[5:16].replace('T', ' ')}"
            tasks.append(dict(id=tid,label=names.get(tid) or f"未命名任务 · {last[5:16].replace('T',' ')}",
                named=bool(names.get(tid)),archived=archived.get(tid,False),last_used=last,system=system))
        return dict(tasks=tasks,models=[r[0] for r in db.execute('SELECT DISTINCT model FROM daily ORDER BY model')],
            has_rows=bool(tasks),scanned_files=len(cached),excluded_legacy_threads=len(legacy-modern-recovered),issues=sorted(errors),
            retry=bool(errors & {'部分日志暂时无法读取','任务标题数据库暂不可读，使用已有名称'}),
            coverage=dict(legacy_threads=len(legacy-modern),recovered_legacy_threads=len(recovered),partial_legacy_threads=len(partial),
                excluded_reasons=dict(reasons),reconstructed_calls=reconstructed,cumulative_gaps=gaps))

    def names(self, errors):
        names, archived, system_tasks = {}, {}, set()
        try:
            with (self.home/'session_index.jsonl').open(encoding='utf-8') as stream:
                for line in stream:
                    try:
                        item = json.loads(line)
                        if valid_id(item.get('id')) and isinstance(item.get('thread_name'),str):
                            names[item['id']] = item['thread_name'][:160]
                    except (ValueError,TypeError,AttributeError): pass
        except OSError: pass
        database = self.home/'state_5.sqlite'
        if database.is_file():
            try:
                with closing(sqlite3.connect(database.resolve().as_uri()+'?mode=ro',uri=True,timeout=1)) as db:
                    db.execute('PRAGMA cache_size=-256'); db.execute('PRAGMA mmap_size=0')
                    columns = {r[1] for r in db.execute('PRAGMA table_info(threads)')}
                    if {'id','title','archived'} <= columns:
                        name = 'name' if 'name' in columns else 'NULL'
                        source = 'source' if 'source' in columns else 'NULL'
                        for tid,name,title,is_archived,source in db.execute(f'SELECT id,substr({name},1,512),substr(title,1,512),archived,{source} FROM threads'):
                            label = name or title
                            if valid_id(tid) and isinstance(label,str) and label.strip(): names[tid] = ' '.join(label.split())[:160]
                            archived[tid] = bool(is_archived)
                            try:
                                data=json.loads(source) if source else {}
                                if isinstance(data,dict) and data.get('subagent',{}).get('other')=='guardian': system_tasks.add(tid)
                            except (ValueError,TypeError,AttributeError): pass
            except sqlite3.Error: errors.add('任务标题数据库暂不可读，使用已有名称')
        return names,archived,system_tasks

    def query(self, days='30', model='all', task='all', group='model', now=None, summary_only=False):
        if days not in ('1','7','30','90','all') or group not in ('model','task'):
            raise ValueError('Invalid filters')
        today=(now or datetime.now(TZ)).astimezone(TZ).date()
        start=(today-timedelta(days=int(days)-1)).isoformat() if days!='all' else ''
        with closing(self.connect()) as db:
            db.execute('BEGIN')  # Pin metadata and aggregates to the same committed scan.
            stored=db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()
            if stored is None:
                return super().query(days,model,task,group,now,summary_only)
            data=json.loads(stored[0]); tasks,models=data['tasks'],data['models']
            if model!='all' and model not in models: raise ValueError('Unknown model')
            if task!='all' and task not in {t['id'] for t in tasks}: raise ValueError('Unknown task')
            meta=dict(application='codex-token-usage-dashboard',generated_at=data['generated_at'],time_zone='UTC+08:00',
                refresh_seconds=self.refresh_seconds,refresh_error=self.refresh_error,scan_duration_ms=self.scan_duration_ms,
                loading=False,**{k:data[k] for k in ('scanned_files','excluded_legacy_threads','issues','coverage')})
            selected_task=next((t for t in tasks if t['id']==task),None)
            clause='date>=? AND date<=?'; params=[start,today.isoformat()]
            if model!='all': clause+=' AND model=?'; params.append(model)
            eligible_clause,eligible_params=clause,list(params)
            if task!='all': clause+=' AND task=?'; params.append(task)
            aggregate=f'{sums()},COALESCE(SUM(requests),0) AS requests,COUNT(DISTINCT task) AS active_tasks'
            summary=finish_total(db.execute(f'SELECT {aggregate} FROM daily WHERE {clause}',params).fetchone())
            if not data['has_rows']: summary={k:None for k in summary}
            if summary_only:
                return dict(meta={k:meta[k] for k in ('generated_at','time_zone','refresh_seconds','loading','refresh_error')},
                    summary=summary,filters=dict(selected_task=selected_task))
            recent={r[0]:r[1] for r in db.execute(f'SELECT task,MAX(timestamp) FROM daily WHERE {eligible_clause} GROUP BY task',eligible_params)}
            choices=[dict(t,last_used=recent[t['id']]) for t in tasks if t['id'] in recent]
            choices.sort(key=lambda t:(t['last_used'],t['id']),reverse=True)
            dates={r['date']:finish_total(r) for r in db.execute(f'SELECT date,{aggregate} FROM daily WHERE {clause} GROUP BY date',params)}
            zero=finish_total(dict.fromkeys(FIELDS+('requests','active_tasks'),0))
            begin=datetime.fromisoformat(start or min(dates,default=today.isoformat())).date()
            timeline=[]
            while data['has_rows'] and begin<=today:
                day=begin.isoformat(); timeline.append(dict(dates.get(day,zero),date=day)); begin+=timedelta(days=1)
            labels={t['id']:t['label'] for t in tasks}
            groups=[]
            for r in db.execute(f'SELECT {group} AS id,{aggregate} FROM daily WHERE {clause} GROUP BY {group} ORDER BY SUM(total_tokens) DESC',params):
                row=finish_total(r); row['label']=labels.get(row['id'],row['id']) if group=='task' else row['id']; groups.append(row)
            if data['excluded_legacy_threads']:
                meta['issues'].append(f"{data['excluded_legacy_threads']} 个旧格式任务尚无法安全纳入，具体原因见全局覆盖诊断")
            if not data['has_rows']:
                meta['issues'].append('暂无可核实的用量记录；当前用量不可统计，不能解释为零消耗')
            return dict(meta=meta,filters=dict(models=models,tasks=choices,selected_task=selected_task),summary=summary,timeline=timeline,groups=groups)
