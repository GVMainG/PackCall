import pathlib,json,subprocess as sp,time,uuid
ROOT=pathlib.Path.cwd();OUT=ROOT/'_bmad-output/implementation-artifacts/story-11-1-test-email-operator'
RUN=pathlib.Path((OUT/'w22-http/latest-run.txt').read_text());PROJECT=json.loads((RUN/'results.json').read_text())['project'];records=[]
if json.loads((RUN/'results.json').read_text()).get('status')!='passed':raise SystemExit('Refusing failed W22 run')
def cmd(args,check=True,timeout=100):
 r=sp.run(args,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=timeout)
 with (OUT/'shutdown-commands.jsonl').open('a') as f:f.write(json.dumps(dict(args=args,exit=r.returncode,stdout=r.stdout,stderr=r.stderr))+'\n')
 if check and r.returncode:raise RuntimeError(r.stderr)
 return r.stdout.strip()
def sql(q):return cmd(['docker','exec',PROJECT+'-db-1','psql','-U','acceptance','-d','acceptance','-Atc',q])
def wait(fn,seconds=15):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  if fn():return
  time.sleep(.1)
 raise AssertionError('bounded wait')
published=OUT/'probe-publish';cmd(['dotnet','publish','tests/PackCall.Worker.Probe','-c','Release','-o',str(published)])
cmd(['docker','start',PROJECT+'-db-1']);time.sleep(2)
try:
 for mode in ['inprocess','http']:
  for probe in ['before','ignore']:
   sql('TRUNCATE deliveries,campaigns,recipients,delivery_status_journal CASCADE')
   cid=str(uuid.uuid4())
   sql(f"insert into campaigns(id,name,message_text,status,created_at,updated_at,started_at) values('{cid}','Shutdown','synthetic',1,clock_timestamp(),clock_timestamp(),clock_timestamp()); with r as(insert into recipients(id,email) values(gen_random_uuid(),'shutdown@example.test') returning id,email) insert into deliveries select gen_random_uuid(),'{cid}',id,email,0,clock_timestamp(),clock_timestamp(),null,null,null from r")
   name=PROJECT+'-shutdown-'+mode+'-'+probe
   args=['docker','run','-d','--name',name,'--network',PROJECT+'_default','-v',str(published)+':/probe:ro','--entrypoint','dotnet']
   for key,value in dict(PACKCALL_ENVIRONMENT='test',TEST_SENDER_MODE=mode,TEST_OPERATOR_URL='http://operator:8000',PROBE_MODE=probe,ConnectionStrings__Postgres='Host=db;Database=acceptance;Username=acceptance;Password=acceptance').items():args+=['-e',key+'='+value]
   cmd(args+['packcallworker','/probe/PackCall.Worker.Probe.dll'])
   wait(lambda:sp.run(['docker','exec',name,'test','-f','/tmp/packcall-probe-entered'],capture_output=True).returncode==0)
   before=sql('select row_to_json(d) from deliveries d');journal=sql('select row_to_json(j) from delivery_status_journal j')
   start=time.monotonic();cmd(['docker','stop','--time','40',name]);elapsed=time.monotonic()-start
   assert elapsed<33
   assert sql('select row_to_json(d) from deliveries d')==before
   assert sql('select row_to_json(j) from delivery_status_journal j')==journal
   assert sql('select count(*) from deliveries where delivery_status=1')=='1'
   records.append(dict(mode=mode,probe=probe,seconds=elapsed,leaseRetained=True,journalUnchanged=True))
finally:cmd(['docker','stop',PROJECT+'-db-1'])
(OUT/'shutdown-probes.json').write_text(json.dumps(records,indent=2));print('Host shutdown before/in uncooperative sender both modes PASS')
