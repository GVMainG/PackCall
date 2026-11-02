from pathlib import Path
import json,re,ast
root=Path.cwd();e=root/'tests/reorganization-evidence';mapping={}
def move(old,new):
 a=root/old;b=root/new;assert a.resolve().is_relative_to(root) and b.resolve().is_relative_to(root);b.parent.mkdir(parents=True,exist_ok=True);a.rename(b);mapping[old]=new
for old,new in [('tests/acceptance/story-2-1-worker','tests/acceptance/workers/lifecycle'),('tests/acceptance/story-2-2-workers','tests/acceptance/workers/coordination'),('tests/acceptance/story-11-1-test-email-operator','tests/acceptance/operators')]:
 for p in list((root/old).rglob('*')):
  if p.is_file() and '__pycache__' not in p.parts:move(p.relative_to(root).as_posix(),new+'/'+p.relative_to(root/old).as_posix())
for name in ['alternate.py','delayed.py']:
 old='tests/acceptance/operators/'+name;new='tests/acceptance/operators/fixtures/'+name;move(old,new)
 for k,v in list(mapping.items()):
  if v==old:mapping[k]=new
move('tests/acceptance/workers/coordination/clock.c','tests/acceptance/workers/coordination/fixtures/clock.c')
mapping['tests/acceptance/story-2-2-workers/clock.c']='tests/acceptance/workers/coordination/fixtures/clock.c'
move('tests/acceptance/operators/test_harness_fixes.py','tests/tools/operators/test_harness_fixes.py');mapping['tests/acceptance/story-11-1-test-email-operator/test_harness_fixes.py']='tests/tools/operators/test_harness_fixes.py'
for p in list((root/'scripts/performance').glob('test_*.py')):
 target='tests/acceptance/measurements/postgres_repeat.py' if p.name=='test_postgres_repeat.py' else 'tests/tools/performance/'+p.name
 move(p.relative_to(root).as_posix(),target)
for name in ['control_smoke.py','crash_smoke.py','resource_smoke.py','stall_smoke.py','two_worker_crash.py']:move('scripts/performance/'+name,'tests/acceptance/measurements/'+name)
replacements=[('tests/acceptance/story-2-1-worker','tests/acceptance/workers/lifecycle'),('tests/acceptance/story-2-2-workers','tests/acceptance/workers/coordination'),('tests/acceptance/story-11-1-test-email-operator','tests/acceptance/operators')]+[(a,b) for a,b in mapping.items() if a.startswith('scripts/performance/')]
for base in [root/'tests/acceptance',root/'tests/tools',root/'scripts/performance',root/'PackCall.Test']:
 for p in base.rglob('*'):
  if not p.is_file() or '__pycache__' in p.parts or 'bin' in p.parts or 'obj' in p.parts or p.suffix not in ('.py','.md','.yml','.csproj') and p.name!='Dockerfile':continue
  s=p.read_text(encoding='utf-8');original=s
  for a,b in replacements:s=s.replace(a,b)
  s=s.replace('tests/acceptance/operators/alternate.py','tests/acceptance/operators/fixtures/alternate.py').replace('tests/acceptance/operators/delayed.py','tests/acceptance/operators/fixtures/delayed.py').replace('tests/acceptance/workers/coordination/clock.c','tests/acceptance/workers/coordination/fixtures/clock.c')
  if '/workers/' in p.as_posix() and p.suffix=='.yml':s=s.replace('context: ../../..','context: ../../../..')
  if s!=original:p.write_text(s,encoding='utf-8')
bootstrap='import sys\nfrom pathlib import Path as _Path\n_PERFORMANCE = _Path(__file__).resolve().parents[3] / "scripts/performance"\nsys.path.insert(0, str(_PERFORMANCE))\n'
for base in [root/'tests/tools/performance',root/'tests/acceptance/measurements']:
 for p in base.glob('*.py'):
  s=p.read_text(encoding='utf-8');tree=ast.parse(s);lines=s.splitlines(keepends=True);at=tree.body[0].end_lineno if isinstance(tree.body[0],ast.Expr) and isinstance(tree.body[0].value,ast.Constant) and isinstance(tree.body[0].value.value,str) else 0
  s=''.join(lines[:at])+bootstrap+''.join(lines[at:])
  s=s.replace('ROOT = Path(__file__).resolve().parent','ROOT = _PERFORMANCE').replace('Path(__file__).with_name("run.py")','(_PERFORMANCE / "run.py")').replace('Path(__file__).resolve().parents[2] / "artifacts/performance"','_PERFORMANCE.parents[1] / "artifacts/performance"')
  p.write_text(s,encoding='utf-8')
# Consolidate only the demonstrated equivalent Docker helper.
a=root/'tests/acceptance/workers/lifecycle/startup.py';b=root/'tests/acceptance/workers/lifecycle/shutdown-probes.py'
sa=a.read_text(encoding='utf-8');sb=b.read_text(encoding='utf-8');pattern=r'def docker\(\*a,check=True\):\n.*?(?=def sql)';ha=re.search(pattern,sa,re.S).group();hb=re.search(pattern,sb,re.S).group();assert ast.dump(ast.parse(ha))==ast.dump(ast.parse(hb))
(a.parent/'support.py').write_text('import subprocess as sp\n\n'+ha,encoding='utf-8')
a.write_text(sa.replace(ha,'from support import docker\n\n'),encoding='utf-8');b.write_text(sb.replace(hb,'from support import docker\n\n'),encoding='utf-8')
(e/'python-source-mapping.json').write_text(json.dumps(mapping,indent=2))
p=root/'.gitignore';s=p.read_text(encoding='utf-8');s=s.replace('/tests/*\n!/tests/PackCall.Worker.Probe/\n!/tests/PackCall.Workers.Acceptance/\n!/tests/acceptance/\n/tests/acceptance/*\n!/tests/acceptance/story-2-2-workers/\n!/tests/acceptance/story-11-1-test-email-operator/\n','');s+='\n!/tests/reorganization-evidence/**/*.log\n';p.write_text(s,encoding='utf-8')
