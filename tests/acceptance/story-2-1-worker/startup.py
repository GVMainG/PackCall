import subprocess as sp,time,json
from pathlib import Path
out=Path('_bmad-output/implementation-artifacts/story-2-1-worker')
out.mkdir(parents=True, exist_ok=True)
network='packcall-w21_default'; db='packcall-w21-packcall.db-1'; results={}
def docker(*a,check=True):
 r=sp.run(['docker',*a],text=True,encoding="utf-8",capture_output=True)
 if check and r.returncode:raise RuntimeError(r.stderr)
 return r.stdout.strip()
def sql(q): return docker('exec',db,'psql','-U','packcall','-d','packcall','-Atc',q)
def wait(check,seconds=15):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  v=check()
  if v:return v
  time.sleep(.1)
 raise AssertionError('Timed out')
def start(name,app,database='packcall',extra=()):
 docker('rm','-f',name,check=False)
 docker('run','-d','--name',name,'--network',network,'-e',f'ConnectionStrings__Postgres=Host=packcall.db;Database={database};Username=packcall;Password=packcall',*extra,'packcall'+app.lower())
def exitcode(name):
 wait(lambda:docker('inspect','-f','{{.State.Running}}',name)=='false',70)
 return int(docker('inspect','-f','{{.State.ExitCode}}',name))
for database in ['w21empty','w21await']:
 sql('DROP DATABASE IF EXISTS '+database+' WITH (FORCE)'); sql('CREATE DATABASE '+database)
# Empty schema can become ready within deadline; neither gate applies migrations.
start('w21-await','Worker','w21await');wait(lambda:'Applied=[]' in docker('logs','w21-await'))
results['gateNoDdl']=docker('exec',db,'psql','-U','packcall','-d','w21await','-Atc',"select count(*) from pg_tables where schemaname='public'")
assert results['gateNoDdl']=='0'
start('w21-prepare','Migrator','w21await');assert exitcode('w21-prepare')==0
wait(lambda:'Schema ready' in docker('logs','w21-await'))
results['waitThenReady']=True;docker('stop','w21-await')
# SIGTERM while gate waits: no worker loop/HTTP starts.
for app in ['Api','Worker']:
 name='w21-gate-'+app.lower();start(name,app,'w21empty')
 wait(lambda:'Applied=[]' in docker('logs',name))
 t=time.monotonic();docker('kill','--signal','TERM',name);code=exitcode(name)
 logs=docker('logs',name)
 assert 'Schema ready' not in logs and 'Parallelism=' not in logs
 results[app+'GateSigterm']={'exit':code,'seconds':round(time.monotonic()-t,3)}
# A standalone unavailable database is still gated; actual TERM cancels the wait.
for app in ['Api','Worker']:
 name='w21-unavailable-'+app.lower()
 start(name,app,extra=('-e','ConnectionStrings__Postgres=Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1'))
 time.sleep(3)
 assert docker('inspect','-f','{{.State.Running}}',name)=='true'
 assert 'Schema ready' not in docker('logs',name) and 'Parallelism=' not in docker('logs',name)
 docker('kill','--signal','TERM',name);code=exitcode(name);assert code!=0
 results[app+'UnavailableSigtermExit']=code
# Exact schema history, reject unknown immediately.
sql('INSERT INTO "__EFMigrationsHistory" VALUES (\'20990101_Unknown\',\'10.0.4\')')
for app in ['Api','Worker']:
 name='w21-unknown-'+app.lower();t=time.monotonic();start(name,app)
 code=exitcode(name);assert code!=0
 results[app+'Unknown']={'exit':code,'seconds':round(time.monotonic()-t,3)}
sql('DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId"=\'20990101_Unknown\'')
# Repeat migrator and its true SIGTERM while waiting for history table lock.
start('w21-repeat','Migrator');assert exitcode('w21-repeat')==0;results['migratorRepeatExit']=0
block=sp.Popen(['docker','exec','-i',db,'psql','-U','packcall','-d','packcall'],stdin=sp.PIPE,stdout=sp.DEVNULL,stderr=sp.DEVNULL,text=True)
block.stdin.write('BEGIN; LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE; SELECT pg_sleep(40);\n');block.stdin.flush()
wait(lambda:sql("select count(*) from pg_stat_activity where query like '%pg_sleep(40)%' and state='active' and pid<>pg_backend_pid()")=='1')
start('w21-migrator-cancel','Migrator')
wait(lambda:int(sql("select count(*) from pg_stat_activity where wait_event_type='Lock'"))>0)
docker('kill','--signal','TERM','w21-migrator-cancel');code=exitcode('w21-migrator-cancel');assert code!=0
results['migratorSigtermExit']=code
# Release blocker immediately, no forced cleanup of production data.
sql("select pg_terminate_backend(pid) from pg_stat_activity where query like '%pg_sleep(40)%' and state='active' and pid<>pg_backend_pid()")
block.wait(timeout=10)
# A genuinely empty schema must fail at the full 60s startup deadline.
start('w21-timeout','Worker','w21empty');t=time.monotonic();code=exitcode('w21-timeout');assert code!=0
results['emptyTimeout']={'exit':code,'seconds':round(time.monotonic()-t,3)}
(out/'startup.json').write_text(json.dumps(results,indent=2));print(json.dumps(results,indent=2))
