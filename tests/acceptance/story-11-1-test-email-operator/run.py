"""Bounded acceptance; fresh isolated projects only. Never removes volumes."""
import xml.etree.ElementTree as ET
import os, subprocess as sp, json, uuid, time, urllib.request, pathlib, datetime
ROOT=pathlib.Path.cwd()
OUT=ROOT/'_bmad-output/implementation-artifacts/story-11-1-test-email-operator'
RUN=OUT/('run-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
RUN.mkdir(parents=True)
ENV=dict(os.environ,PACKCALL_ENVIRONMENT='test',TEST_SENDER_MODE='inprocess')
for key in ['TEST_SIMULATE_ERRORS','TEST_OPERATOR_URL','TEST_OPERATOR_TIMEOUT_SECONDS']:ENV.pop(key,None)
results={"status":"running"}
(RUN/"results.json").write_text(json.dumps(results))
(OUT/"latest-run.txt").write_text(str(RUN))

def cmd(args, env=None, check=True, timeout=180):
    try:r=sp.run(args,capture_output=True,text=True,encoding='utf-8',errors='replace',env=env or ENV,timeout=timeout)
    except sp.TimeoutExpired as e:
        with (RUN/'commands.jsonl').open('a') as f:f.write(json.dumps({'args':args,'timeout':timeout,'stdout':str(e.stdout),'stderr':str(e.stderr)})+'\n')
        raise
    with (RUN/'commands.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps({'args':args,'exit':r.returncode,'stdout':r.stdout,'stderr':r.stderr})+'\n')
    if check and r.returncode:raise RuntimeError(str(args)+': '+r.stderr[-1000:]+r.stdout[-1000:])
    return r

def wait(fn, seconds=35):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            value=fn()
            if value:return value
        except Exception:pass
        time.sleep(.1)
    raise AssertionError('bounded wait expired')

try:
    base=['docker','compose','-f','docker-compose.yml']
    # Required interpolation is checked with cleaned process environment.
    for key in ['PACKCALL_ENVIRONMENT','TEST_SENDER_MODE']:
        for value in [None,'']:
            env=dict(ENV)
            if value is None:env.pop(key,None)
            else:env[key]=value
            r=cmd(base+['config'],env,check=False)
            assert r.returncode!=0 and key in r.stderr
    results['CFG11-05']='passed'
    # Build actual published host images plus external operator after the lock is fixed.
    cmd(base+['-f','docker-compose.test-http.yml','-p','packcall-b11-build','build'],timeout=600)
    for host in ['Api','Worker','Migrator']:
        cmd(['dotnet','publish','src/PackCall.'+host,'-c','Release','-o',str(OUT/'publish'/host)],timeout=180)
    # Standalone images: bad config cannot touch an unavailable DB or contact an operator.
    for host in ['api','worker','migrator']:
        image='packcall'+host
        cases=[]
        for key in ['PACKCALL_ENVIRONMENT','TEST_SENDER_MODE']:
            for value in [None,'','   ','unknown']:
                config={'PACKCALL_ENVIRONMENT':'test','TEST_SENDER_MODE':'inprocess'}
                if value is None:config.pop(key)
                else:config[key]=value
                cases.append((config,key))
        cases += [({'PACKCALL_ENVIRONMENT':'prod','DOTNET_ENVIRONMENT':'Development'},'PACKCALL_ENVIRONMENT=prod is not supported'),
                  ({'PACKCALL_ENVIRONMENT':'test','TEST_SENDER_MODE':'http','TEST_OPERATOR_URL':'http://user:marker@localhost/?query=marker'},'TEST_OPERATOR_URL')]
        for config, expected in cases:
            args=['docker','run','--rm','--network','none','-w','/tmp','--entrypoint','dotnet']
            for key,value in config.items():args+=['-e',key+'='+value]
            r=cmd(args+[image,'/app/PackCall.'+host.title()+'.dll'],check=False,timeout=15)
            assert r.returncode==1 and expected in r.stderr and 'marker' not in r.stderr
    results['CFG11-02-prod-unavailable']='passed'
    
    for mode in ['inprocess','http']:
        project='packcall-b11-'+mode+'-'+uuid.uuid4().hex[:6]
        env=dict(ENV,TEST_SENDER_MODE=mode)
        compose=['docker','compose','-p',project,'-f','docker-compose.yml']
        if mode=='http':
            compose+=['-f','docker-compose.test-http.yml']
            env['TEST_OPERATOR_URL']='http://packcall.test-email-operator:8000'
        compose+=['-f','tests/acceptance/story-11-1-test-email-operator/compose.acceptance.yml']
        def dc(*args):return cmd(compose+list(args),env)
        def sql(query):return cmd(['docker','exec',project+'-packcall.db-1','psql','-U','packcall','-d','packcall','-Atc',query]).stdout.strip()
        def api(path, body=None):
            port=cmd(['docker','port',project+'-packcall.api-1','8080/tcp']).stdout.strip().split(':')[-1]
            req=urllib.request.Request('http://127.0.0.1:'+port+'/api/'+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json'})
            with urllib.request.urlopen(req,timeout=5) as r:return json.load(r)
        try:
            config=json.loads(dc('config','--format','json').stdout)
            for service in ['packcall.api','packcall.worker','packcall.migrator']:
                actual=config['services'][service]['environment']
                assert actual['PACKCALL_ENVIRONMENT']=='test' and actual['TEST_SENDER_MODE']==mode and str(actual['TEST_SIMULATE_ERRORS']).lower()=='false'
            if mode=='inprocess':assert 'packcall.test-email-operator' not in config['services']
            dc('up','-d','packcall.db')
            wait(lambda:sql("select count(*) from information_schema.tables where table_schema='public'")=='0')
            for host in ['api','worker','migrator']:
                r=cmd(['docker','run','--rm','--network',project+'_default','-e','PACKCALL_ENVIRONMENT=prod','-e','TEST_SENDER_MODE=invalid','-e','ConnectionStrings__Postgres=Host=packcall.db;Database=packcall;Username=packcall;Password=packcall','packcall'+host],check=False)
                assert r.returncode==1 and 'PACKCALL_ENVIRONMENT=prod is not supported' in r.stderr
            assert sql("select count(*) from information_schema.tables where table_schema='public'")=='0'
            dc('up','-d','--no-build')
            wait(lambda:api('campaigns') is not None)
            # Real API creates the entire delivery snapshot; no direct fixture SQL for happy path.
            for i in range(3):api('recipients',{'email':f'b11-{i}@example.test'})
            campaign=api('campaigns',{'name':'B11 smoke','messageText':'synthetic-marker'})
            api('campaigns/'+campaign['id']+'/start',{})
            wait(lambda:sql('select count(*) from deliveries where delivery_status=2')=='3')
            wait(lambda:sql('select count(*) from delivery_status_journal where new_status=2')=='3')
            assert sql('select status from campaigns')=='2'
            snapshot=sql("select json_build_object('deliveries',(select json_agg(d) from deliveries d),'journal',(select json_agg(j) from delivery_status_journal j),'campaigns',(select json_agg(c) from campaigns c))")
            (RUN/(mode+'-terminal.json')).write_text(snapshot)
            if mode=='inprocess':
                port=cmd(['docker','port',project+'-packcall.db-1','5432/tcp']).stdout.strip().split(':')[-1]
                tests=dict(ENV,PACKCALL_TEST_POSTGRES='Host=127.0.0.1;Port='+port+';Database=packcall;Username=packcall;Password=packcall')
                cmd(['dotnet','test','PackCall.Core.Test','--logger','trx;LogFileName=full.trx','--results-directory',str(RUN/'results')],tests,timeout=300)
                counters=ET.parse(RUN/'results/full.trx').find('.//{*}Counters').attrib
                assert int(counters['total'])>0 and counters['passed']==counters['total'] and int(counters.get('failed',0))==0 and int(counters.get('notExecuted',0))==0,counters
                (RUN/'test-counters.json').write_text(json.dumps(counters,indent=2))
            else:
                operator=project+'-packcall.test-email-operator-1'
                assert json.loads(cmd(['docker','inspect',operator]).stdout)[0]['Mounts']==[]
                before=cmd(['docker','diff',operator]).stdout
                probe="import json,urllib.request; p=dict(delivery_id='11111111-1111-1111-1111-111111111111',campaign_id='22222222-2222-2222-2222-222222222222',recipient_id='33333333-3333-3333-3333-333333333333',recipient_email='marker@example.test',message_text='privacy-marker',simulate_errors=False); [urllib.request.urlopen(urllib.request.Request('http://localhost:8000/v1/email/send',data=json.dumps(p).encode(),headers={'Content-Type':'application/json'})).read() for _ in range(5)]"
                cmd(['docker','exec',operator,'python','-c',probe])
                assert cmd(['docker','diff',operator]).stdout==before
                cmd(['docker','restart',operator])
                wait(lambda:json.loads(cmd(['docker','inspect',operator]).stdout)[0]['State']['Health']['Status']=='healthy')
                assert cmd(['docker','diff',operator]).stdout==before
                logs=cmd(['docker','logs',operator])
                (RUN/'operator-logs.json').write_text(json.dumps(dict(stdout=logs.stdout,stderr=logs.stderr)))
                assert logs.stdout.strip()=='' and logs.stderr.strip()==''
                # Change only the URL; a separate external fixture proves actual destination.
                alternate=project+'-alternate'
                fixture=str(ROOT/'tests/acceptance/story-11-1-test-email-operator/alternate.py')
                cmd(['docker','run','-d','--name',alternate,'--network',project+'_default','--network-alias','alternate','-v',fixture+':/fixture.py:ro','python:3.12.10-slim','python','-u','/fixture.py'])
                dc('stop','packcall.worker','packcall.test-email-operator')
                changed=project+'-changed-worker'
                try:
                    cmd(compose+['run','-d','--no-deps','--name',changed,'-e','TEST_OPERATOR_URL=http://alternate:8000','packcall.worker'],env)
                    campaign=api('campaigns',{'name':'Changed URL','messageText':'synthetic-marker'})
                    api('campaigns/'+campaign['id']+'/start',{})
                    wait(lambda:sql('select count(*) from deliveries where delivery_status=2')=='6')
                    wait(lambda:sql('select count(*) from delivery_status_journal where new_status=2')=='6')
                    assert cmd(['docker','logs',alternate]).stdout.count('fixture-called')==3
                finally:
                    cmd(['docker','stop',changed,alternate],check=False)
            # Prod priority is independent of an already successful Migrator and ready DB.
            for host in ['api','worker','migrator']:
                r=cmd(['docker','run','--rm','--network',project+'_default','-e','PACKCALL_ENVIRONMENT=prod','-e','ConnectionStrings__Postgres=Host=packcall.db;Database=packcall;Username=packcall;Password=packcall','packcall'+host],check=False)
                assert r.returncode==1 and 'PACKCALL_ENVIRONMENT=prod is not supported' in r.stderr
            assert sql('select count(*) from deliveries where delivery_status=2')==('3' if mode=='inprocess' else '6')
            results[mode]='passed'
        finally:
            (RUN/(mode+'-compose.log')).write_text(dc('logs','--no-color').stdout,encoding='utf-8')
            dc('stop')
    results['status']='passed'
except BaseException as error:
    results['status']='failed'
    results['error']=repr(error)
    raise
finally:
    (RUN/'results.json').write_text(json.dumps(results,indent=2))
    (OUT/'latest-run.txt').write_text(str(RUN))
print(json.dumps(results,indent=2))
