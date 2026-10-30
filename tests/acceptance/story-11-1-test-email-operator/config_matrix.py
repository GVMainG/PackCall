import os,subprocess as sp,json,pathlib
OUT=pathlib.Path('_bmad-output/implementation-artifacts/story-11-1-test-email-operator')
records=[]
def run(args,env):
 r=sp.run(args,env=env,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
 records.append(dict(args=args,exit=r.returncode,stdout=r.stdout,stderr=r.stderr));return r
base=dict(os.environ,PACKCALL_ENVIRONMENT='test')
for mode,overlay,success in [('inprocess',False,True),('http',True,True),('http',False,False),('inprocess',True,False),(' ',False,False),('unknown',True,False)]:
 env=dict(base,TEST_SENDER_MODE=mode,TEST_OPERATOR_URL='http://packcall.test-email-operator:8000')
 args=['python','tests/acceptance/story-11-1-test-email-operator/preflight.py','-p','packcall-b11-config','-f','docker-compose.yml']
 if overlay:args+=['-f','docker-compose.test-http.yml']
 assert (run(args,env).returncode==0)==success
for value in [None,'','false','true']:
 env=dict(base,TEST_SENDER_MODE='inprocess')
 if value is None:env.pop('TEST_SIMULATE_ERRORS',None)
 else:env['TEST_SIMULATE_ERRORS']=value
 r=run(['docker','compose','-f','docker-compose.yml','config','--format','json'],env);assert r.returncode==0
 services=json.loads(r.stdout)['services']
 flags=[str(services[host]['environment']['TEST_SIMULATE_ERRORS']).lower() for host in ['packcall.api','packcall.worker','packcall.migrator']]
 assert flags==[value if value in ('true','false') else 'false']*3
for host in ['api','worker','migrator']:
 args=['docker','run','--rm','--network','none','-e','PACKCALL_ENVIRONMENT=test','-e','TEST_SENDER_MODE=unknown','packcall'+host,'--TEST_SENDER_MODE=inprocess']
 r=run(args,base);assert r.returncode==1 and 'InvalidOperationException' in r.stderr and 'TEST_SENDER_MODE:' not in r.stderr
 for standard in ['Development','Production']:
  args=['docker','run','--rm','--network','none','-e','PACKCALL_ENVIRONMENT=test','-e','TEST_SENDER_MODE=inprocess','-e','ConnectionStrings__Postgres=','-e','DOTNET_ENVIRONMENT='+standard,'-e','ASPNETCORE_ENVIRONMENT='+standard,'packcall'+host]
  r=run(args,base);assert r.returncode==1 and 'InvalidOperationException' in r.stderr
(OUT/'configuration-matrix.json').write_text(json.dumps(records,indent=2),encoding='utf-8');print('Configuration matrix PASS')
