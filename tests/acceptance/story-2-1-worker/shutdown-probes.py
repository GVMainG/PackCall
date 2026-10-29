import subprocess as sp,json,time,os,uuid
from pathlib import Path
out=Path('_bmad-output/implementation-artifacts/story-2-1-worker')
out.mkdir(parents=True, exist_ok=True);results={};db='packcall-w21-packcall.db-1'
def docker(*a,check=True):
 r=sp.run(['docker',*a],text=True,encoding='utf-8',capture_output=True)
 if check and r.returncode:raise RuntimeError(r.stderr)
 return r.stdout.strip()
def sql(q):return docker('exec',db,'psql','-U','packcall','-d','packcall','-Atc',q)
docker('stop','packcall-w21-packcall.worker-1')
for mode in ['before','ignore']:
 cid=str(uuid.uuid4());did=str(uuid.uuid4())
 sql(f"INSERT INTO campaigns (id,name,message_text,status,created_at,updated_at,started_at) VALUES ('{cid}','probe','test',1,clock_timestamp(),clock_timestamp(),clock_timestamp()); INSERT INTO deliveries(id,campaign_id,recipient_id,recipient_email,delivery_status,created_at,updated_at) SELECT '{did}','{cid}',id,email,0,clock_timestamp(),clock_timestamp() FROM recipients LIMIT 1;")
 name='w21-probe-'+mode;docker('rm','-f',name,check=False)
 mount=str(Path(os.environ['TEMP'])/'packcall-w21-probe')+':/probe:ro'
 docker('run','-d','--name',name,'--network','packcall-w21_default','-v',mount,'-e','PROBE_MODE='+mode,'-e','ConnectionStrings__Postgres=Host=packcall.db;Database=packcall;Username=packcall;Password=packcall','--entrypoint','dotnet','packcallworker','/probe/PackCall.Worker.Probe.dll')
 end=time.monotonic()+15
 while time.monotonic()<end:
  q=sp.run(['docker','exec',name,'test','-f','/tmp/packcall-probe-entered'],stdout=sp.DEVNULL,stderr=sp.DEVNULL)
  if q.returncode==0:break
  time.sleep(.1)
 else:raise AssertionError(docker('logs',name))
 snapshot=sql(f"select delivery_status,worker_id,lock_expires_at from deliveries where id='{did}'");assert snapshot.startswith('1|')
 t=time.monotonic();docker('stop','--time','40',name);elapsed=time.monotonic()-t
 assert elapsed<40
 after=sql(f"select delivery_status,worker_id,lock_expires_at from deliveries where id='{did}'");assert after==snapshot
 assert sql(f"select count(*) from delivery_status_journal where delivery_id='{did}' and new_status in (2,3)")=='0'
 results[mode]={'seconds':round(elapsed,3),'exit':docker('inspect','-f','{{.State.ExitCode}}',name),'leaseRetained':True,'noTerminalJournal':True}
(out/'shutdown-probes.json').write_text(json.dumps(results,indent=2));print(json.dumps(results,indent=2))
