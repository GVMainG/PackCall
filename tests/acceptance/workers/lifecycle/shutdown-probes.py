import subprocess as sp,json,time,os,sys,uuid
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from email_fixture import email_deliveries
out=Path('_bmad-output/implementation-artifacts/story-2-1-worker')
out.mkdir(parents=True, exist_ok=True);results={};db='packcall-w21-packcall.db-1'
from support import docker,HOST_ENV

def sql(q):return docker('exec',db,'psql','-U','packcall','-d','packcall','-At','-v','ON_ERROR_STOP=1','-c',q)
docker('stop','packcall-w21-packcall.worker-1')
for mode in ['before','ignore']:
 cid=str(uuid.uuid4());did=str(uuid.uuid4())
 sql(email_deliveries(cid, 'probe', delivery_id=did))
 name='w21-probe-'+mode;docker('rm','-f',name,check=False)
 mount=str(Path(os.environ['TEMP'])/'packcall-w21-probe')+':/probe:ro'
 docker('run','-d','--name',name,'--network','packcall-w21_default','-v',mount,'-e','PROBE_MODE='+mode,'-e','ConnectionStrings__Postgres=Host=packcall.db;Database=packcall;Username=packcall;Password=packcall',*HOST_ENV,'--entrypoint','dotnet','packcallworker','/probe/PackCall.Worker.Probe.dll')
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
 assert sql(f"select count(*) from delivery_status_journal where delivery_id='{did}' and new_status in (2,-1)")=='0'
 results[mode]={'seconds':round(elapsed,3),'exit':docker('inspect','-f','{{.State.ExitCode}}',name),'leaseRetained':True,'noTerminalJournal':True}
(out/'shutdown-probes.json').write_text(json.dumps(results,indent=2));print(json.dumps(results,indent=2))
