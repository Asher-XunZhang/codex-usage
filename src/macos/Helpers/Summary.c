// Bounded native SQLite summary reader. It never reads credentials or conversation bodies.
#include <sqlite3.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <math.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
static void string(const char *value) { if(!value){fputs("null",stdout);return;} putchar('"'); for(const unsigned char *p=(const unsigned char *)value;*p;p++){switch(*p){case '"':fputs("\\\"",stdout);break;case '\\':fputs("\\\\",stdout);break;case '\n':fputs("\\n",stdout);break;case '\r':fputs("\\r",stdout);break;case '\t':fputs("\\t",stdout);break;default:if(*p<32)printf("\\u%04x",*p);else putchar(*p);}}putchar('"'); }
static const char *option(int argc,char **argv,const char *key,const char *fallback){for(int i=1;i+1<argc;i++)if(!strcmp(argv[i],key))return argv[i+1];return fallback;}
static time_t query_time;
static const char *fields[]={"input_tokens","output_tokens","total_tokens","cached_input_tokens","reasoning_output_tokens","cache_write_input_tokens"};
static int query(sqlite3 *db,const char *days,const char *model,const char *task,int refresh,int has_rows,const char *stamp){
 int count=!strcmp(days,"all")?0:atoi(days);if(strcmp(days,"all")&&count!=1&&count!=7&&count!=30&&count!=90)return 0;
 time_t now=query_time+8*3600;struct tm date;gmtime_r(&now,&date);char end[16],start[16]="";strftime(end,sizeof(end),"%Y-%m-%d",&date);
 if(count){now-=(count-1)*86400;gmtime_r(&now,&date);strftime(start,sizeof(start),"%Y-%m-%d",&date);}
 char sql[2500]="SELECT ";for(int i=0;i<6;i++){char part[220];snprintf(part,sizeof(part),"%sCASE WHEN COUNT(%s)=COUNT(*) THEN COALESCE(SUM(%s),0) END",i?",":"",fields[i],fields[i]);strlcat(sql,part,sizeof(sql));}
 strlcat(sql,",COALESCE(SUM(requests),0),COUNT(DISTINCT task) FROM daily WHERE date>=? AND date<=? AND (?='all' OR model=?) AND (?='all' OR task=?)",sizeof(sql));
 sqlite3_stmt *s=NULL;if(sqlite3_prepare_v2(db,sql,-1,&s,NULL)!=SQLITE_OK)return 0;
 const char *args[]={start,end,model,model,task,task};for(int i=0;i<6;i++)sqlite3_bind_text(s,i+1,args[i],-1,SQLITE_STATIC);
 if(sqlite3_step(s)!=SQLITE_ROW){sqlite3_finalize(s);return 0;}
 fputs("{\"meta\":{\"generated_at\":",stdout);string(stamp);printf(",\"time_zone\":\"UTC+08:00\",\"refresh_seconds\":%d,\"loading\":false,\"refresh_error\":null},\"summary\":{",refresh);
 for(int i=0;i<8;i++){if(i)putchar(',');string(i<6?fields[i]:(i==6?"requests":"active_tasks"));putchar(':');if(!has_rows||sqlite3_column_type(s,i)==SQLITE_NULL)fputs("null",stdout);else printf("%lld",sqlite3_column_int64(s,i));}
 int known=has_rows&&sqlite3_column_type(s,0)!=SQLITE_NULL&&sqlite3_column_type(s,3)!=SQLITE_NULL;
 fputs(",\"noncached_input_tokens\":",stdout);if(known)printf("%lld",sqlite3_column_int64(s,0)-sqlite3_column_int64(s,3));else fputs("null",stdout);
 fputs(",\"cache_hit_rate\":",stdout);if(known&&sqlite3_column_int64(s,0))printf("%.17g",sqlite3_column_double(s,3)/sqlite3_column_double(s,0)*100);else fputs("null",stdout);
 sqlite3_finalize(s);fputs("},\"filters\":{\"selected_task\":",stdout);
 if(strcmp(task,"all")&&sqlite3_prepare_v2(db,"SELECT j.value FROM kv,json_each(kv.value,'$.tasks') j WHERE kv.key='snapshot' AND json_extract(j.value,'$.id')=?",-1,&s,NULL)==SQLITE_OK){sqlite3_bind_text(s,1,task,-1,SQLITE_STATIC);if(sqlite3_step(s)==SQLITE_ROW)fputs((const char *)sqlite3_column_text(s,0),stdout);else fputs("null",stdout);sqlite3_finalize(s);}else fputs("null",stdout);
 fputs("}}",stdout);return 1;
}
static int choices(sqlite3 *db,const char *kind,const char *days,const char *model,const char *task){
 if(strcmp(kind,"model")&&strcmp(kind,"task"))return 0;
 if(strcmp(days,"1")&&strcmp(days,"7")&&strcmp(days,"30")&&strcmp(days,"90")&&strcmp(days,"all"))return 0;
 time_t now=query_time+8*3600;struct tm date;gmtime_r(&now,&date);char end[16],start[16]="";strftime(end,sizeof(end),"%Y-%m-%d",&date);
 if(strcmp(days,"all")){now-=(atoi(days)-1)*86400;gmtime_r(&now,&date);strftime(start,sizeof(start),"%Y-%m-%d",&date);}
 // Only cached IDs, titles and daily aggregate keys are queried. No record/log
 // body is read, and selecting a task never narrows its own choice list.
 const char *sql=!strcmp(kind,"model") ?
  "SELECT j.value,j.value FROM kv,json_each(kv.value,'$.models') j WHERE kv.key='snapshot'" :
  "WITH recent AS (SELECT task,MAX(timestamp) AS last_used FROM daily WHERE date>=? AND date<=? AND (?='all' OR model=?) GROUP BY task),"
  " titles AS (SELECT json_extract(j.value,'$.id') AS id,COALESCE(json_extract(j.value,'$.label'),json_extract(j.value,'$.id')) AS label FROM kv,json_each(kv.value,'$.tasks') j WHERE kv.key='snapshot')"
  " SELECT titles.id,titles.label FROM titles LEFT JOIN recent ON recent.task=titles.id WHERE recent.task IS NOT NULL OR titles.id=? ORDER BY recent.last_used DESC,titles.id DESC";
 sqlite3_stmt *s=NULL;if(sqlite3_prepare_v2(db,sql,-1,&s,NULL)!=SQLITE_OK)return 0;
 if(!strcmp(kind,"task")){const char *args[]={start,end,model,model,task};for(int i=0;i<5;i++)sqlite3_bind_text(s,i+1,args[i],-1,SQLITE_STATIC);}
 fputs("{\"choices\":[{\"id\":\"all\",\"label\":",stdout);string(!strcmp(kind,"model")?"全部模型":"全部任务");putchar('}');
 int step;while((step=sqlite3_step(s))==SQLITE_ROW){fputs(",{\"id\":",stdout);string((const char *)sqlite3_column_text(s,0));fputs(",\"label\":",stdout);string((const char *)sqlite3_column_text(s,1));putchar('}');}
 fputs("]}\n",stdout);sqlite3_finalize(s);return step==SQLITE_DONE;
}

// Budget requests are a bounded batch over the same committed index snapshot.
// They deliberately use usage, whose rows have already been deduplicated and
// attributed to root tasks, rather than the dashboard's daily aggregates.
#define BUDGET_MAX_INPUT 65536
#define BUDGET_MAX_REQUESTS 50
#define BUDGET_MAX_ROWS 1024
typedef struct {
 char id[129],period[257],model[513],task[513];
 sqlite3_int64 revision;
 double start,end;
} BudgetRequest;

static int budget_deadline(void *context){
 struct timespec now;clock_gettime(CLOCK_MONOTONIC,&now);
 return now.tv_sec>((struct timespec *)context)->tv_sec+5;
}

static int budget_text(sqlite3_stmt *s,int column,char *out,size_t capacity){
 if(sqlite3_column_type(s,column)!=SQLITE_TEXT)return 0;
 const unsigned char *value=sqlite3_column_text(s,column);
 int size=sqlite3_column_bytes(s,column);
 if(!value||size<1||(size_t)size>=capacity||strlen((const char *)value)!=(size_t)size)return 0;
 for(int i=0;i<size;i++)if(value[i]<32||value[i]==127)return 0;
 memcpy(out,value,(size_t)size+1);return 1;
}

static int budget_requests(sqlite3 *db,const char *path,BudgetRequest *requests,int *count){
 int fd=open(path,O_RDONLY|O_NONBLOCK|O_CLOEXEC);if(fd<0)return 0;
 struct stat info;
 if(fstat(fd,&info)||!S_ISREG(info.st_mode)||info.st_size<1||info.st_size>BUDGET_MAX_INPUT){close(fd);return 0;}
 FILE *file=fdopen(fd,"rb");if(!file){close(fd);return 0;}
 char *body=malloc(BUDGET_MAX_INPUT+1);if(!body){fclose(file);return 0;}
 size_t size=fread(body,1,BUDGET_MAX_INPUT+1,file);int valid=!ferror(file)&&size<=BUDGET_MAX_INPUT&&size>0;
 fclose(file);if(!valid){free(body);return 0;}body[size]='\0';
 if(strlen(body)!=size){free(body);return 0;}
 // Older SQLite JSON1 versions truncate decoded strings at a NUL, before
 // budget_text can compare their byte length. Reject the JSON escape first;
 // consume other escape pairs so a literal backslash followed by u0000 is valid.
 for(size_t i=0;i<size;i++)if(body[i]=='\\'){
  if(size-i>=6&&!memcmp(body+i,"\\u0000",6)){free(body);return 0;}
  if(i+1<size)i++;
 }
 sqlite3_stmt *s=NULL;
 const char *check="SELECT json_valid(?1)";
 if(sqlite3_prepare_v2(db,check,-1,&s,NULL)!=SQLITE_OK){free(body);return 0;}
 sqlite3_bind_text(s,1,body,(int)size,SQLITE_STATIC);
 valid=sqlite3_step(s)==SQLITE_ROW&&sqlite3_column_int(s,0);sqlite3_finalize(s);s=NULL;
 if(!valid){free(body);return 0;}
 check="SELECT json_type(?1)='object' AND json_type(?1,'$.requests')='array' AND "
       "(SELECT COUNT(*) FROM json_each(?1))=1,json_array_length(?1,'$.requests')";
 if(sqlite3_prepare_v2(db,check,-1,&s,NULL)!=SQLITE_OK){free(body);return 0;}
 sqlite3_bind_text(s,1,body,(int)size,SQLITE_STATIC);
 valid=sqlite3_step(s)==SQLITE_ROW&&sqlite3_column_int(s,0);
 *count=valid?sqlite3_column_int(s,1):0;
 valid=valid&&*count>=0&&*count<=BUDGET_MAX_REQUESTS;sqlite3_finalize(s);s=NULL;
 if(!valid){free(body);return 0;}
 const char *sql="SELECT j.type,json_extract(j.value,'$.id'),json_extract(j.value,'$.revision'),"
  "json_extract(j.value,'$.periodID'),json_extract(j.value,'$.start'),json_extract(j.value,'$.end'),"
  "json_extract(j.value,'$.model'),json_extract(j.value,'$.task'),"
  "(SELECT COUNT(*)=7 AND COUNT(DISTINCT key)=7 FROM json_each(j.value)) AND "
  "json_type(j.value,'$.id')='text' AND json_type(j.value,'$.periodID')='text' AND "
  "json_type(j.value,'$.model')='text' AND json_type(j.value,'$.task')='text' AND "
  "json_type(j.value,'$.revision')='integer' AND json_type(j.value,'$.start') IN ('integer','real') AND "
  "json_type(j.value,'$.end') IN ('integer','real') "
  "FROM json_each(?1,'$.requests') j";
 if(sqlite3_prepare_v2(db,sql,-1,&s,NULL)!=SQLITE_OK){free(body);return 0;}
 sqlite3_bind_text(s,1,body,(int)size,SQLITE_STATIC);
 int step,index=0;
 while((step=sqlite3_step(s))==SQLITE_ROW){
  if(index>=*count){valid=0;break;}
  BudgetRequest *r=&requests[index];
  valid=sqlite3_column_type(s,0)==SQLITE_TEXT&&!strcmp((const char *)sqlite3_column_text(s,0),"object")&&
   sqlite3_column_int(s,8)&&budget_text(s,1,r->id,sizeof(r->id))&&budget_text(s,3,r->period,sizeof(r->period))&&
   budget_text(s,6,r->model,sizeof(r->model))&&budget_text(s,7,r->task,sizeof(r->task))&&
   sqlite3_column_type(s,2)==SQLITE_INTEGER&&sqlite3_column_int64(s,2)>=0&&
   (sqlite3_column_type(s,4)==SQLITE_INTEGER||sqlite3_column_type(s,4)==SQLITE_FLOAT)&&
   (sqlite3_column_type(s,5)==SQLITE_INTEGER||sqlite3_column_type(s,5)==SQLITE_FLOAT);
  if(!valid)break;
  r->revision=sqlite3_column_int64(s,2);r->start=sqlite3_column_double(s,4);r->end=sqlite3_column_double(s,5);
  valid=isfinite(r->start)&&isfinite(r->end)&&r->start>=-62135596800.0&&r->end<=253402300799.0&&r->end>r->start;
  for(int previous=0;valid&&previous<index;previous++)if(!strcmp(requests[previous].id,r->id))valid=0;
  if(!valid)break;index++;
 }
 valid=valid&&step==SQLITE_DONE&&index==*count;
 sqlite3_finalize(s);free(body);return valid;
}

static int budgets(sqlite3 *db,const char *path,int has_rows){
 BudgetRequest requests[BUDGET_MAX_REQUESTS];int count=0;
 if(!budget_requests(db,path,requests,&count)){fputs("Invalid budget requests\n",stderr);return 2;}
 struct timespec started;clock_gettime(CLOCK_MONOTONIC,&started);
 sqlite3_progress_handler(db,10000,budget_deadline,&started);
 sqlite3_stmt *s=NULL,*aggregate=NULL,*scope=NULL;
 const char *metadata="SELECT (julianday(json_extract(value,'$.generated_at'))-2440587.5)*86400.0,"
  "COALESCE(json_array_length(value,'$.issues'),0),COALESCE(json_extract(value,'$.excluded_legacy_threads'),0),"
  "COALESCE(json_extract(value,'$.coverage.partial_legacy_threads'),0),"
  "COALESCE(json_array_length(value,'$.coverage.cumulative_gaps'),0),"
  "(SELECT COUNT(*) FROM usage WHERE julianday(timestamp) IS NULL) FROM kv WHERE key='snapshot'";
 if(sqlite3_prepare_v2(db,metadata,-1,&s,NULL)!=SQLITE_OK||sqlite3_step(s)!=SQLITE_ROW||sqlite3_column_type(s,0)==SQLITE_NULL)goto unavailable;
 double generated=sqlite3_column_double(s,0);
 if(!isfinite(generated))goto unavailable;
 sqlite3_int64 issue_count=sqlite3_column_int64(s,1),excluded=sqlite3_column_int64(s,2),partial=sqlite3_column_int64(s,3),gaps=sqlite3_column_int64(s,4),invalid_time=sqlite3_column_int64(s,5);
 int complete=has_rows&&!issue_count&&!excluded&&!partial&&!gaps&&!invalid_time;
 sqlite3_finalize(s);s=NULL;
 char sql[6000]="SELECT model,COUNT(*)";
 for(int i=0;i<6;i++){
  char part[600];snprintf(part,sizeof(part),",CASE WHEN COUNT(%s)=COUNT(*) THEN SUM(%s) END,COUNT(%s),COALESCE(SUM(%s),0)",fields[i],fields[i],fields[i],fields[i]);
  strlcat(sql,part,sizeof(sql));
 }
 // julianday normalizes Z and numeric offsets to the same instant. The Python
 // index owns the matching expression index; this SELECT never migrates it.
 strlcat(sql," FROM usage WHERE julianday(timestamp)>=julianday(?1,'unixepoch') AND julianday(timestamp)<julianday(?2,'unixepoch') AND (?3='all' OR model=?3) AND (?4='all' OR task=?4) GROUP BY model ORDER BY model LIMIT 1025",sizeof(sql));
 const char *scope_sql="SELECT (?1='all' OR EXISTS(SELECT 1 FROM json_each(k.value,'$.models') j WHERE j.value=?1)) AND "
  "(?2='all' OR EXISTS(SELECT 1 FROM json_each(k.value,'$.tasks') j WHERE json_extract(j.value,'$.id')=?2)) FROM kv k WHERE k.key='snapshot'";
 if(sqlite3_prepare_v2(db,sql,-1,&aggregate,NULL)!=SQLITE_OK||sqlite3_prepare_v2(db,scope_sql,-1,&scope,NULL)!=SQLITE_OK)goto unavailable;
 printf("{\"generated_at\":%.17g,\"has_rows\":%s,\"coverage_complete\":%s,\"coverage\":{\"issue_count\":%lld,\"excluded_legacy_threads\":%lld,\"partial_legacy_threads\":%lld,\"cumulative_gap_count\":%lld,\"invalid_timestamp_rows\":%lld,\"issues_truncated\":%s},\"issues\":[",
  generated,has_rows?"true":"false",complete?"true":"false",issue_count,excluded,partial,gaps,invalid_time,issue_count>32?"true":"false");
 const char *issues="SELECT substr(j.value,1,512) FROM kv,json_each(kv.value,'$.issues') j WHERE kv.key='snapshot' AND j.type='text' LIMIT 32";
 if(sqlite3_prepare_v2(db,issues,-1,&s,NULL)!=SQLITE_OK)goto unavailable;
 int step,issue_index=0;
 while((step=sqlite3_step(s))==SQLITE_ROW){if(issue_index++)putchar(',');string((const char *)sqlite3_column_text(s,0));}
 if(step!=SQLITE_DONE)goto unavailable;
 sqlite3_finalize(s);s=NULL;fputs("],\"results\":[",stdout);
 int row_count=0;
 for(int index=0;index<count;index++){
  BudgetRequest *r=&requests[index];
  sqlite3_bind_text(scope,1,r->model,-1,SQLITE_STATIC);sqlite3_bind_text(scope,2,r->task,-1,SQLITE_STATIC);
  if(sqlite3_step(scope)!=SQLITE_ROW)goto unavailable;
  int valid=sqlite3_column_int(scope,0);sqlite3_reset(scope);sqlite3_clear_bindings(scope);
  if(index)putchar(',');fputs("{\"id\":",stdout);string(r->id);printf(",\"revision\":%lld,\"periodID\":",r->revision);string(r->period);
  printf(",\"start\":%.17g,\"end\":%.17g,\"model\":",r->start,r->end);string(r->model);fputs(",\"task\":",stdout);string(r->task);
  printf(",\"scope_valid\":%s,\"coverage_complete\":%s,\"issues\":[",valid?"true":"false",valid&&complete?"true":"false");
  if(!valid)string("统计范围中的模型或任务已不存在");
  else if(!has_rows)string("暂无可核实的用量记录，不能解释为零消耗");
  else if(!complete)string("索引存在全局覆盖缺失，本区间仅能确认已记录的用量");
  fputs("],\"rows\":[",stdout);
  if(valid){
   sqlite3_bind_double(aggregate,1,r->start);sqlite3_bind_double(aggregate,2,r->end);
   sqlite3_bind_text(aggregate,3,r->model,-1,SQLITE_STATIC);sqlite3_bind_text(aggregate,4,r->task,-1,SQLITE_STATIC);
   int result_rows=0;
   while((step=sqlite3_step(aggregate))==SQLITE_ROW){
    if(++row_count>BUDGET_MAX_ROWS||sqlite3_column_bytes(aggregate,0)>512)goto unavailable;
    if(result_rows++)putchar(',');fputs("{\"model\":",stdout);string((const char *)sqlite3_column_text(aggregate,0));
    printf(",\"requests\":%lld",sqlite3_column_int64(aggregate,1));
    for(int field=0;field<6;field++){
     int column=2+field*3;putchar(',');string(fields[field]);putchar(':');
     if(sqlite3_column_type(aggregate,column)==SQLITE_NULL)fputs("null",stdout);else printf("%lld",sqlite3_column_int64(aggregate,column));
     printf(",\"known_%s\":%lld,\"lower_bound_%s\":%lld",fields[field],sqlite3_column_int64(aggregate,column+1),fields[field],sqlite3_column_int64(aggregate,column+2));
    }
    putchar('}');
   }
   if(step!=SQLITE_DONE)goto unavailable;
   sqlite3_reset(aggregate);sqlite3_clear_bindings(aggregate);
  }
  fputs("]}",stdout);
 }
 fputs("]}\n",stdout);sqlite3_finalize(aggregate);sqlite3_finalize(scope);sqlite3_progress_handler(db,0,NULL,NULL);return 0;
unavailable:
 fprintf(stderr,"Budget snapshot unavailable: %s\n",sqlite3_errmsg(db));
 if(s)sqlite3_finalize(s);if(aggregate)sqlite3_finalize(aggregate);if(scope)sqlite3_finalize(scope);sqlite3_progress_handler(db,0,NULL,NULL);return 75;
}

int main(int argc,char **argv){
 const char *clock=option(argc,argv,"--now",NULL);query_time=clock?(time_t)strtoll(clock,NULL,10):time(NULL);
 const char *path=option(argc,argv,"--cache-path",NULL),*days=option(argc,argv,"--days","30"),*model=option(argc,argv,"--model","all"),*task=option(argc,argv,"--task","all");int refresh=atoi(option(argc,argv,"--refresh-seconds","0"));
 if(!path||(argc>1&&!strcmp(argv[argc-1],"--budgets-file")))return 2;sqlite3 *db=NULL;if(sqlite3_open_v2(path,&db,SQLITE_OPEN_READWRITE|SQLITE_OPEN_NOMUTEX,NULL)!=SQLITE_OK){if(db)sqlite3_close(db);return 75;}
 // Apple SQLite may need to create WAL coordination sidecars even for a SELECT.
 // Do not create a missing database, and enforce read-only SQL after opening.
 sqlite3_busy_timeout(db,2000);if(sqlite3_exec(db,"PRAGMA query_only=ON;PRAGMA cache_size=-128;PRAGMA mmap_size=0;PRAGMA temp_store=FILE;BEGIN",NULL,NULL,NULL)!=SQLITE_OK){sqlite3_close(db);return 75;}
 sqlite3_stmt *s=NULL;const char *sql="SELECT json_extract(value,'$.generated_at'),json_extract(value,'$.has_rows') FROM kv WHERE key='snapshot'";
 if(sqlite3_prepare_v2(db,sql,-1,&s,NULL)!=SQLITE_OK||sqlite3_step(s)!=SQLITE_ROW){fprintf(stderr,"snapshot query failed: %s\n",sqlite3_errmsg(db));if(s)sqlite3_finalize(s);sqlite3_close(db);return 75;}
 const char *raw=(const char *)sqlite3_column_text(s,0);char *stamp=strdup(raw?raw:"");int has_rows=sqlite3_column_int(s,1);sqlite3_finalize(s);
 const char *budget_path=option(argc,argv,"--budgets-file",NULL);if(budget_path){int code=budgets(db,budget_path,has_rows);free(stamp);sqlite3_close(db);return code;}
 const char *kind=option(argc,argv,"--choices",NULL);if(kind){int ok=choices(db,kind,days,model,task);free(stamp);sqlite3_close(db);return ok?0:2;}
 int reset=0;const char *check="SELECT (?='all' OR EXISTS(SELECT 1 FROM json_each(kv.value,'$.models') j WHERE j.value=?)) AND (?='all' OR EXISTS(SELECT 1 FROM json_each(kv.value,'$.tasks') j WHERE json_extract(j.value,'$.id')=?)) FROM kv WHERE key='snapshot'";
 if(sqlite3_prepare_v2(db,check,-1,&s,NULL)==SQLITE_OK){sqlite3_bind_text(s,1,model,-1,SQLITE_STATIC);sqlite3_bind_text(s,2,model,-1,SQLITE_STATIC);sqlite3_bind_text(s,3,task,-1,SQLITE_STATIC);sqlite3_bind_text(s,4,task,-1,SQLITE_STATIC);reset=sqlite3_step(s)!=SQLITE_ROW||!sqlite3_column_int(s,0);sqlite3_finalize(s);}
 if(reset){model="all";task="all";}
 fputs("{\"today\":",stdout);int ok=query(db,"1","all","all",refresh,has_rows,stamp);fputs(",\"filtered\":",stdout);ok=ok&&query(db,days,model,task,refresh,has_rows,stamp);printf(",\"filter_reset\":%s}\n",reset?"true":"false");free(stamp);sqlite3_close(db);return ok?0:1;
}
