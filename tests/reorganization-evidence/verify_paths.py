from pathlib import Path
import json,ast,re,compileall,subprocess,xml.etree.ElementTree as ET
root=Path.cwd();e=root/'tests/reorganization-evidence';mapping=json.loads((e/'python-source-mapping.json').read_text());original=json.loads((e/'original-sources.json').read_text(encoding='utf-8'))['files']
full=[]
for f in original:
 if not f['path'].endswith('.py'):continue
 target=mapping.get(f['path'],f['path']);s=(root/target).read_text(encoding='utf-8');before=f['source']
 for a,b in [('tests/acceptance/story-2-1-worker','tests/acceptance/workers/lifecycle'),('tests/acceptance/story-2-2-workers','tests/acceptance/workers/coordination'),('tests/acceptance/story-11-1-test-email-operator','tests/acceptance/operators'),('tests/acceptance/operators/alternate.py','tests/acceptance/operators/fixtures/alternate.py'),('tests/acceptance/operators/delayed.py','tests/acceptance/operators/fixtures/delayed.py'),('PackCall.Core.Test','PackCall.Test')]:before=before.replace(a,b)
 bootstrap='import sys\nfrom pathlib import Path as _Path\n_PERFORMANCE = _Path(__file__).resolve().parents[3] / "scripts/performance"\nsys.path.insert(0, str(_PERFORMANCE))\n';s=s.replace(bootstrap,'')
 if 'from support import docker' in s:
  helper=re.search(r'def docker\(\*a,check=True\):\n.*?(?=def sql)',before,re.S).group();s=s.replace('from support import docker\n\n',helper)
 before=before.replace('ROOT = Path(__file__).resolve().parent','ROOT = _PERFORMANCE').replace('Path(__file__).with_name("run.py")','(_PERFORMANCE / "run.py")').replace('Path(__file__).resolve().parents[2] / "artifacts/performance"','_PERFORMANCE.parents[1] / "artifacts/performance"') if target!=f['path'] and f['path'].startswith('scripts/performance') else before
 assert ast.dump(ast.parse(before))==ast.dump(ast.parse(s)),f['path']
 full.append({'old':f['path'],'new':target,'top_level_steps_and_all_ast_preserved_with_explicit_path_updates':True})
(e/'python-full-ast-comparison.json').write_text(json.dumps(full,indent=2))
assert compileall.compile_dir(root/'tests/acceptance',quiet=1)
assert compileall.compile_dir(root/'tests/tools',quiet=1)
# Check all moved immutable assets against recorded original content, with relocation-only substitutions.
assets=[]
asset_originals=[f for f in original if not f['path'].endswith(('.py','.cs','.csproj','.md'))]
# requirements.txt was not included in the initial source snapshot; recover its exact baseline bytes.
requirements_old='tests/acceptance/story-11-1-test-email-operator/requirements.txt'
raw=subprocess.check_output(['git','show','e5c41ed93656f1bdb5669f35abd932310ecae85b:'+requirements_old])
asset_originals.append({'path':requirements_old,'source':raw.decode('utf-8').replace('\r\n','\n'),'sha256':__import__('hashlib').sha256(raw).hexdigest()})
for f in asset_originals:
 target=mapping.get(f['path'],f['path']);p=root/target
 before=f['source'];expected=before
 allowed=[]
 if f['path'].startswith('tests/acceptance/story-2-2-workers/') and (p.suffix=='.yml' or p.name=='Dockerfile'):
  for a,b in [('tests/acceptance/story-2-2-workers/clock.c','tests/acceptance/workers/coordination/fixtures/clock.c'),('tests/acceptance/story-2-2-workers','tests/acceptance/workers/coordination')]:expected=expected.replace(a,b)
  if p.suffix=='.yml':expected=expected.replace('context: ../../..','context: ../../../..')
  allowed=['coordination source/Dockerfile relocation','one additional parent level in coordination Compose build contexts']
 assert p.read_text(encoding='utf-8')==expected,(f['path'],target,'asset content changed')
 if expected==before and f['path']!=requirements_old:assert __import__('hashlib').sha256(p.read_bytes()).hexdigest()==f['sha256'],(target,'immutable asset bytes changed')
 if f['path']==requirements_old:
  expected_blob=subprocess.check_output(['git','rev-parse','e5c41ed93656f1bdb5669f35abd932310ecae85b:'+requirements_old],text=True).strip()
  actual_blob=subprocess.check_output(['git','hash-object','--path='+target,str(p)],text=True).strip()
  assert expected_blob==actual_blob,'Requirements git content changed'
 assets.append({'old':f['path'],'new':target,'original_sha256':f['sha256'],'final_sha256':__import__('hashlib').sha256(p.read_bytes()).hexdigest(),'content_equal_after_explicit_relocations':True,'allowed_changes':allowed})
(e/'nonpython-asset-comparison.json').write_text(json.dumps(assets,indent=2))
# Verify moved Compose contexts, Dockerfile COPY sources and literal repository file references.
import yaml
checks=[]
for p in (root/'tests/acceptance').rglob('*.yml'):
 data=yaml.safe_load(p.read_text(encoding='utf-8')) or {}
 for name,service in data.get('services',{}).items():
  build=service.get('build')
  if not build:continue
  context=(p.parent/(build if isinstance(build,str) else build.get('context','.'))).resolve();assert context.exists(),(p,context)
  if isinstance(build,dict) and 'dockerfile' in build:assert (context/build['dockerfile']).exists(),(p,build)
  checks.append({'compose':p.relative_to(root).as_posix(),'service':name,'context':context.relative_to(root).as_posix()})
for p in (root/'tests/acceptance').rglob('Dockerfile'):
 for source in re.findall(r'^COPY (tests/\S+) ',p.read_text(),re.M):assert (root/source).exists(),(p,source)
for base in [root/'tests/acceptance',root/'tests/tools',root/'scripts/performance']:
 for p in base.rglob('*.py'):
  for value in (n.value for n in ast.walk(ast.parse(p.read_text(encoding='utf-8'))) if isinstance(n,ast.Constant) and isinstance(n.value,str)):
   if value.startswith(('tests/acceptance/','PackCall.Test/')) and not any(c in value for c in ('\n','*','{')):assert (root/value).exists(),(p,value)
(e/'static-path-checks.json').write_text(json.dumps({'python_compile':True,'all_moved_python_ast_equal':True,'literal_fixture_and_runner_paths_exist':True,'docker_copy_sources_exist':True,'nonpython_assets_compared':len(assets),'compose_builds':checks},indent=2))
print('Full Python AST and static path checks passed:',len(full),'files')
