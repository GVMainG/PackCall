import pathlib,subprocess as sp,json
OUT=pathlib.Path('_bmad-output/implementation-artifacts/story-11-1-test-email-operator').resolve()
fixture=OUT/'configuration-fixture.json';fixture.write_text(json.dumps({'PACKCALL_ENVIRONMENT':' TEST ','TEST_SENDER_MODE':' InProcess '}))
records=[]
for host in ['api','worker','migrator']:
 for key in ['PACKCALL_ENVIRONMENT','TEST_SENDER_MODE']:
  for value in ['', 'unknown']:
   args=['docker','run','--rm','--network','none','-v',str(fixture)+':/app/appsettings.packcall.json:ro','-e',key+'='+value,'packcall'+host]
   r=sp.run(args,capture_output=True,text=True,timeout=15);records.append(dict(args=args,exit=r.returncode,stdout=r.stdout,stderr=r.stderr))
   assert r.returncode==1 and key+':' in r.stderr
  args=['docker','run','--rm','--network','none','-v',str(fixture)+':/app/appsettings.packcall.json:ro','-e',key+'=unknown','-e','ConnectionStrings__Postgres=','packcall'+host,'--'+key+'='+('test' if key=='PACKCALL_ENVIRONMENT' else 'inprocess')]
  r=sp.run(args,capture_output=True,text=True,timeout=15);records.append(dict(args=args,exit=r.returncode,stdout=r.stdout,stderr=r.stderr))
  assert r.returncode==1 and 'InvalidOperationException' in r.stderr and key+':' not in r.stderr
(OUT/'precedence.json').write_text(json.dumps(records,indent=2));print('JSON / environment / CLI precedence all hosts PASS')
