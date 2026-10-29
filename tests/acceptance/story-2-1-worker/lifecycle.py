import subprocess as sp,json,time,urllib.request
from pathlib import Path
out=Path('_bmad-output/implementation-artifacts/story-2-1-worker')
out.mkdir(parents=True, exist_ok=True)
def docker(*a): return sp.check_output(['docker',*a],text=True,stderr=sp.DEVNULL).strip()
def sql(q): return docker('exec','packcall-w21-packcall.db-1','psql','-U','packcall','-d','packcall','-Atc',q)
def wait(check,seconds=15):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  v=check()
  if v:return v
  time.sleep(.05)
 raise AssertionError('Timed out')
def http(path,body=None):
 port=docker('port','packcall-w21-packcall.api-1').split(':')[-1]
 req=urllib.request.Request('http://127.0.0.1:'+port+'/api/'+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=10) as r:return json.load(r)
docker('stop','packcall-w21-packcall.worker-1')
docker('start','packcall-w21-packcall.api-1')
def ready():
 try: return http('campaigns')
 except Exception: return None
wait(ready)
sql('TRUNCATE deliveries,campaigns,delivery_status_journal CASCADE')
c=http('campaigns',{'name':'W21 independent lifecycle','messageText':'test'}); cid=c['id']
http(f'campaigns/{cid}/start',{}); http(f'campaigns/{cid}/pause',{}); http(f'campaigns/{cid}/resume',{})
time.sleep(.5)
r={'apiOnly':http(f'campaigns/{cid}'),'initialSql':sql('select delivery_status,count(*) from deliveries group by delivery_status')}
assert sql('select count(*) from deliveries where delivery_status<>0')=='0'
docker('start','packcall-w21-packcall.worker-1')
def owner():return sql('select worker_id from deliveries where delivery_status=1 limit 1')
wid=wait(owner);r['workerId']=wid
docker('restart','packcall-w21-packcall.api-1');wait(ready);assert owner()==wid
r['apiRestartWorkerUnchanged']=True
docker('stop','packcall-w21-packcall.api-1')
t=time.monotonic();docker('stop','--time','40','packcall-w21-packcall.worker-1');r['stopSeconds']=round(time.monotonic()-t,3)
r['afterSigterm']=sql('select delivery_status,count(*) from deliveries group by delivery_status order by delivery_status')
assert sql('select count(*) from deliveries where delivery_status=3')=='0'
assert int(sql('select count(*) from deliveries where delivery_status=1'))>0
docker('start','packcall-w21-packcall.worker-1');time.sleep(.2)
assert int(sql("select count(*) from deliveries where delivery_status=1 and worker_id='"+wid+"'"))>0
r['unexpiredOldLeaseRetained']=True
r['newWorkerId']=wait(lambda:sql("select worker_id from deliveries where delivery_status=1 and worker_id<>'"+wid+"' limit 1"))
wait(lambda:sql("select status from campaigns where id='"+cid+"'")=='2',115)
r['campaignSqlStatus']=sql("select status from campaigns where id='"+cid+"'")
r['finalDeliveries']=sql('select delivery_status,count(*) from deliveries group by delivery_status')
r['terminalNoResend']=sql('select max(n) from (select count(*) n from delivery_status_journal where new_status=2 group by delivery_id) s')
assert r['finalDeliveries']=='2|1000' and r['terminalNoResend']=='1'
docker('start','packcall-w21-packcall.api-1');wait(ready)
api_started=docker('inspect','-f','{{.State.StartedAt}}','packcall-w21-packcall.api-1')
docker('restart','packcall-w21-packcall.worker-1')
assert docker('inspect','-f','{{.State.StartedAt}}','packcall-w21-packcall.api-1')==api_started
r['workerRestartApiUnchanged']=True
(out/'lifecycle.json').write_text(json.dumps(r,indent=2)); print(json.dumps(r,indent=2))
