import subprocess as sp,os,json,uuid,pathlib,time
OUT=pathlib.Path('_bmad-output/implementation-artifacts/story-11-1-test-email-operator');records=[]
for prepared in [False,True]:
 project='packcall-b11-prod-'+uuid.uuid4().hex[:6];base=['docker','compose','-p',project,'-f','docker-compose.yml']
 env=dict(os.environ,PACKCALL_ENVIRONMENT='test' if prepared else 'prod',TEST_SENDER_MODE='inprocess')
 def cmd(args,check=True):
  r=sp.run(args,env=env,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90)
  records.append(dict(args=args,exit=r.returncode,stdout=r.stdout,stderr=r.stderr))
  if check and r.returncode:raise RuntimeError(r.stderr)
  return r
 def sql(q):return cmd(['docker','exec',project+'-packcall.db-1','psql','-U','packcall','-d','packcall','-Atc',q]).stdout.strip()
 try:
  if prepared:
   cmd(base+['up','-d','--no-build','packcall.migrator'])
   assert cmd(['docker','wait',project+'-packcall.migrator-1']).stdout.strip()=='0'
   assert int(sql("select count(*) from information_schema.tables where table_schema='public'"))>0
   before=sql("select count(*) from information_schema.tables where table_schema='public'")
   env['PACKCALL_ENVIRONMENT']='prod'
  r=cmd(base+['up','-d','--no-build','--force-recreate'],False);assert r.returncode!=0
  inspect=json.loads(cmd(['docker','inspect',project+'-packcall.migrator-1']).stdout)[0]
  assert inspect['State']['ExitCode']==1
  logs=cmd(base+['logs','--no-color']).stdout
  assert 'PACKCALL_ENVIRONMENT=prod is not supported' in logs
  for host in ['api','worker']:
   state=json.loads(cmd(['docker','inspect',project+'-packcall.'+host+'-1']).stdout)[0]['State']
   assert not state['Running'] and state['StartedAt'].startswith('0001-')
  count=sql("select count(*) from information_schema.tables where table_schema='public'")
  assert count==(before if prepared else '0')
 finally:cmd(base+['stop'])
(OUT/'prod-compose.json').write_text(json.dumps(records,indent=2));print('Prod Compose empty/ready/stale-successful-Migrator guard PASS')
