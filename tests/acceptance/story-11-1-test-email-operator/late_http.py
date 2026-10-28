import pathlib,subprocess as sp,json,time,uuid

ROOT=pathlib.Path.cwd();OUT=ROOT/'_bmad-output/implementation-artifacts/story-11-1-test-email-operator'

RUN=pathlib.Path((OUT/'w22-http/latest-run.txt').read_text());PROJECT=json.loads((RUN/'results.json').read_text())['project']
if json.loads((RUN/'results.json').read_text()).get('status')!='passed':raise SystemExit('Refusing failed W22 run')

CONTROL=RUN/'late-control';CONTROL.mkdir()

LOG=RUN/'late-http-commands.jsonl'

def cmd(args,check=True):

 r=sp.run(['docker',*args],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90)

 with LOG.open('a') as f:f.write(json.dumps({'args':args,'exit':r.returncode,'stdout':r.stdout,'stderr':r.stderr})+'\n')

 if check and r.returncode:raise RuntimeError(r.stderr)

 return r.stdout.strip()

def sql(q):return cmd(['exec',PROJECT+'-db-1','psql','-U','acceptance','-d','acceptance','-Atc',q])

def wait(fn,seconds=35):

 end=time.monotonic()+seconds

 while time.monotonic()<end:

  if fn():return

  time.sleep(.05)

 raise AssertionError('bounded wait')

def snapshot():return sql("select json_build_object('deliveries',(select json_agg(d order by id) from deliveries d),'journal',(select json_agg(j order by id) from delivery_status_journal j),'campaigns',(select json_agg(c order by id) from campaigns c))")

old=PROJECT+'-late-old';new=PROJECT+'-late-new';fixture=PROJECT+'-delayed'

def worker(name,url):

 args=['run','-d','--name',name,'--network',PROJECT+'_default']

 for key,value in {'PACKCALL_ENVIRONMENT':'test','TEST_SENDER_MODE':'http','TEST_OPERATOR_URL':url,'TEST_OPERATOR_TIMEOUT_SECONDS':'30','ConnectionStrings__Postgres':'Host=db;Database=acceptance;Username=acceptance;Password=acceptance','DeliveryWorker__Parallelism':'1','DeliveryWorker__BatchSize':'1','DeliveryWorker__LockDuration':'00:00:20','DeliveryWorker__RecoveryInterval':'00:00:00.1','DeliveryWorker__IdleDelay':'00:00:00.1'}.items():args+=['-e',key+'='+value]

 cmd(args+['packcallworker'])

try:

 cmd(['start',PROJECT+'-db-1',PROJECT+'-operator-1']);wait(lambda:cmd(['exec',PROJECT+'-db-1','pg_isready','-U','acceptance'],False).endswith('accepting connections'))

 sql('TRUNCATE deliveries,campaigns,recipients,delivery_status_journal CASCADE')

 cid=str(uuid.uuid4())

 sql(f"insert into campaigns(id,name,message_text,status,created_at,updated_at,started_at) values('{cid}','Late HTTP','synthetic',1,clock_timestamp(),clock_timestamp(),clock_timestamp()); with r as(insert into recipients(id,email) values(gen_random_uuid(),'late@example.test') returning id,email) insert into deliveries select gen_random_uuid(),'{cid}',id,email,0,clock_timestamp(),clock_timestamp(),null,null,null from r")

 script=str(ROOT/'tests/acceptance/story-11-1-test-email-operator/delayed.py')

 cmd(['run','-d','--name',fixture,'--network',PROJECT+'_default','--network-alias','delayed','-v',script+':/fixture.py:ro','-v',str(CONTROL)+':/control','python:3.12.10-slim','python','-u','/fixture.py'])

 worker(old,'http://delayed:8000');wait(lambda:(CONTROL/'http-entered').exists())

 before=snapshot()

 wait(lambda:'истекает; доставка' in cmd(['logs',old]),25)

 assert snapshot()==before

 cmd(['stop','--time','40',old])

 worker(new,'http://operator:8000')

 wait(lambda:sql('select count(*) from delivery_status_journal where new_status=2')=='1',35)

 wait(lambda:sql('select status from campaigns')=='2')

 terminal=snapshot();(CONTROL/'http-release').touch();wait(lambda:(CONTROL/'http-replied').exists())

 time.sleep(.2);assert snapshot()==terminal

 (RUN/'late-http.json').write_text(json.dumps({'status':'passed','before':json.loads(before),'terminal':json.loads(terminal),'after':json.loads(snapshot())},indent=2))

 print('Late HTTP process cancellation/recovery/fence: PASS')

finally:

 for name in [old,new,fixture]:

  logs=cmd(['logs',name],False);(RUN/(name+'.log')).write_text(logs,encoding='utf-8');cmd(['stop',name],False)

 cmd(['stop',PROJECT+'-db-1',PROJECT+'-operator-1'])

