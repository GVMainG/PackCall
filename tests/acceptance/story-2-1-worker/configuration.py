import os,subprocess as sp,json,time,tempfile,shutil
from pathlib import Path
out=Path('_bmad-output/implementation-artifacts/story-2-1-worker')
out.mkdir(parents=True, exist_ok=True);root=Path.cwd();base=Path(tempfile.mkdtemp(prefix='packcall-w21-publish-'))
conn='Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=w21test'
# Publish again after final common-loader change.
r={}
for app in ['Api','Worker','Migrator']:
 d=base/app;sp.run(['dotnet','publish',f'src/PackCall.{app}/PackCall.{app}.csproj','-c','Release','-o',str(d),'--nologo','-v','quiet'],check=True,stdout=sp.DEVNULL)
 shared=d/'appsettings.packcall.json';assert shared.exists();r[app]={'sharedFile':True}
 env=os.environ.copy();env.pop('ConnectionStrings__Postgres',None)
 def run(args=(),environment=env):return sp.run(['dotnet',str(d/f'PackCall.{app}.dll'),*args],cwd=base,env=environment,text=True,encoding='utf-8',capture_output=True,timeout=10)
 assert run().returncode!=0;r[app]['missingConnectionRejected']=True
 shared.rename(shared.with_suffix('.bak'))
 try:
  e=env.copy();e['ConnectionStrings__Postgres']=conn
  assert run(environment=e).returncode!=0;r[app]['missingSharedRejected']=True
 finally:shared.with_suffix('.bak').rename(shared)
# Migrator successfully runs from a different cwd against separate test database.
app='Migrator';d=base/app;env=os.environ.copy();env['ConnectionStrings__Postgres']=conn
assert run(environment=env).returncode==0;r[app]['otherCwdExit']=0
# Worker options are startup validated, with ready schema.
app='Worker';d=base/app
assert run(args=('--DeliveryWorker:BatchSize=0',),environment=env).returncode!=0
r[app]['invalidOptionsRejected']=True
# Verify all five priorities with real published Worker and its observed settings.
shared=d/'appsettings.packcall.json';settings=json.loads(shared.read_text(encoding='utf-8'));settings['DeliveryWorker']={'Parallelism':1};shared.write_text(json.dumps(settings),encoding='utf-8')
host=d/'appsettings.json';host.write_text('{"DeliveryWorker":{"Parallelism":2}}',encoding='utf-8')
(d/'appsettings.Check.json').write_text('{"DeliveryWorker":{"Parallelism":3}}',encoding='utf-8')
for name,expected,envvals,args in [('host',2,{},[]),('environmentJson',3,{'DOTNET_ENVIRONMENT':'Check'},[]),('environmentVariable',4,{'DOTNET_ENVIRONMENT':'Check','DeliveryWorker__Parallelism':'4'},[]),('cli',5,{'DOTNET_ENVIRONMENT':'Check','DeliveryWorker__Parallelism':'4'},['--DeliveryWorker:Parallelism=5'])]:
 e=env.copy();e.update(envvals)
 proc=sp.Popen(['dotnet',str(d/'PackCall.Worker.dll'),*args],cwd=base,env=e,stdout=sp.PIPE,stderr=sp.STDOUT,text=True,encoding='utf-8')
 lines=[];end=time.monotonic()+10
 try:
  while time.monotonic()<end:
   line=proc.stdout.readline();lines.append(line)
   if 'Parallelism=' in line:
    assert 'Parallelism='+str(expected)+',' in line,(name,lines);break
  else:raise AssertionError(lines)
 finally:proc.terminate();proc.wait(timeout=5)
 r[app][name+'Priority']=True
r['publishDirectory']=str(base)
(out/'configuration.json').write_text(json.dumps(r,indent=2));print(json.dumps(r,indent=2))
