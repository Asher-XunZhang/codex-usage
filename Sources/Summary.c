// Bounded native SQLite summary reader. It never reads credentials or conversation bodies.
#include <sqlite3.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
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
int main(int argc,char **argv){
 const char *clock=option(argc,argv,"--now",NULL);query_time=clock?(time_t)strtoll(clock,NULL,10):time(NULL);
 const char *path=option(argc,argv,"--cache-path",NULL),*days=option(argc,argv,"--days","30"),*model=option(argc,argv,"--model","all"),*task=option(argc,argv,"--task","all");int refresh=atoi(option(argc,argv,"--refresh-seconds","0"));
 if(!path)return 2;sqlite3 *db=NULL;if(sqlite3_open_v2(path,&db,SQLITE_OPEN_READWRITE|SQLITE_OPEN_NOMUTEX,NULL)!=SQLITE_OK){if(db)sqlite3_close(db);return 75;}
 // Apple SQLite may need to create WAL coordination sidecars even for a SELECT.
 // Do not create a missing database, and enforce read-only SQL after opening.
 sqlite3_busy_timeout(db,2000);sqlite3_exec(db,"PRAGMA query_only=ON;PRAGMA cache_size=-128;PRAGMA mmap_size=0;PRAGMA temp_store=FILE;BEGIN",NULL,NULL,NULL);
 sqlite3_stmt *s=NULL;const char *sql="SELECT json_extract(value,'$.generated_at'),json_extract(value,'$.has_rows') FROM kv WHERE key='snapshot'";
 if(sqlite3_prepare_v2(db,sql,-1,&s,NULL)!=SQLITE_OK||sqlite3_step(s)!=SQLITE_ROW){fprintf(stderr,"snapshot query failed: %s\n",sqlite3_errmsg(db));if(s)sqlite3_finalize(s);sqlite3_close(db);return 75;}
 const char *raw=(const char *)sqlite3_column_text(s,0);char *stamp=strdup(raw?raw:"");int has_rows=sqlite3_column_int(s,1);sqlite3_finalize(s);
 const char *kind=option(argc,argv,"--choices",NULL);if(kind){int ok=choices(db,kind,days,model,task);free(stamp);sqlite3_close(db);return ok?0:2;}
 int reset=0;const char *check="SELECT (?='all' OR EXISTS(SELECT 1 FROM json_each(kv.value,'$.models') j WHERE j.value=?)) AND (?='all' OR EXISTS(SELECT 1 FROM json_each(kv.value,'$.tasks') j WHERE json_extract(j.value,'$.id')=?)) FROM kv WHERE key='snapshot'";
 if(sqlite3_prepare_v2(db,check,-1,&s,NULL)==SQLITE_OK){sqlite3_bind_text(s,1,model,-1,SQLITE_STATIC);sqlite3_bind_text(s,2,model,-1,SQLITE_STATIC);sqlite3_bind_text(s,3,task,-1,SQLITE_STATIC);sqlite3_bind_text(s,4,task,-1,SQLITE_STATIC);reset=sqlite3_step(s)!=SQLITE_ROW||!sqlite3_column_int(s,0);sqlite3_finalize(s);}
 if(reset){model="all";task="all";}
 fputs("{\"today\":",stdout);int ok=query(db,"1","all","all",refresh,has_rows,stamp);fputs(",\"filtered\":",stdout);ok=ok&&query(db,days,model,task,refresh,has_rows,stamp);printf(",\"filter_reset\":%s}\n",reset?"true":"false");free(stamp);sqlite3_close(db);return ok?0:1;
}
