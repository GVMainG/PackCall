"""Run after run.py; records all regression/build/publish gates against its isolated DB."""
import json, os, pathlib, subprocess as sp, time, xml.etree.ElementTree as ET
out=pathlib.Path(os.environ.get('W22_OUTPUT','_bmad-output/implementation-artifacts/story-2-2-workers'))
run=pathlib.Path((out/'latest-run.txt').read_text())
acceptance=json.loads((run/'results.json').read_text())
if acceptance.get('status')!='passed' or any(acceptance.get('scenarios',{}).get(f'W22-{i:02d}')!='passed' for i in range(1,6)):
    raise SystemExit('Refusing verification gates: run.py must pass all five W22 scenarios first.')
project=acceptance['project']
def execute(args, timeout=1200):
    try:
        return sp.run(args,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=timeout)
    except sp.TimeoutExpired as error:
        def decoded(value): return value.decode('utf-8',errors='replace') if isinstance(value,bytes) else value or ''
        with (run/'gate-timeouts.jsonl').open('a',encoding='utf-8') as f:
            f.write(json.dumps({'command':args,'timeoutSeconds':timeout,'timedOut':True,'stdout':decoded(error.stdout),'stderr':decoded(error.stderr)})+'\n')
        raise
db=project+'-db-1'
inspection=execute(['docker','inspect',db],120)
inspection.check_returncode()
was_running=json.loads(inspection.stdout)[0]['State']['Running']
try:
    if not was_running:execute(['docker','start',db],120).check_returncode()
    deadline=time.monotonic()+40
    while True:
        ready=execute(['docker','exec',db,'pg_isready','-U','acceptance','-d','acceptance'],5)
        if ready.returncode==0:break
        if time.monotonic()>=deadline:raise TimeoutError('Saved PostgreSQL readiness timed out')
        time.sleep(.1)
    port_result=execute(['docker','port',project+'-db-1','5432/tcp'],120)
    port_result.check_returncode()
    port=port_result.stdout.strip().split(':')[-1]
    os.environ['PACKCALL_TEST_POSTGRES']=f'Host=127.0.0.1;Port={port};Database=acceptance;Username=acceptance;Password=acceptance'
    commands=[['dotnet','test','PackCall.Core.Test/PackCall.Core.Test.csproj','--logger','trx;LogFileName=regression.trx','--results-directory',str(run/'test-results')]]
    for role in ['Api','Worker','Migrator']:
        path=f'src/PackCall.{role}/PackCall.{role}.csproj'
        commands += [['dotnet','build',path,'-c','Release'], ['dotnet','publish',path,'-c','Release','-o',str(run/'publish'/role),'/p:UseAppHost=false']]
    commands += [['docker','build','-f','src/PackCall.Worker/Dockerfile','-t',project+'-worker-release','.']]
    results=[]
    for i,args in enumerate(commands):
        p=execute(args)
        (run/f'gate-{i}.log').write_text(p.stdout+'\n'+p.stderr,encoding='utf-8')
        results.append({'command':args,'exitCode':p.returncode})
        (run/'gates.json').write_text(json.dumps(results,indent=2))
        assert p.returncode==0,args
        if i==0:
            tree=ET.parse(run/'test-results/regression.trx')
            counters=tree.find('.//{*}Counters').attrib
            assert counters['total']==counters['passed'] and int(counters.get('notExecuted','0'))==0,counters
            (run/'test-counters.json').write_text(json.dumps(counters,indent=2))
    print(json.dumps(results,indent=2))
finally:
    if not was_running:execute(['docker','stop',db],120).check_returncode()
