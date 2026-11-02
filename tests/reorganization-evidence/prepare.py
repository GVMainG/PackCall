from pathlib import Path
import hashlib,json,ast,shutil,subprocess
root=Path.cwd(); evidence=root/'tests/reorganization-evidence'; old=root/'PackCall.Core.Test'; new=root/'PackCall.Test'
paths=list(old.glob('*.cs'))+list(old.glob('*.csproj'))+[p for base in [root/'tests/acceptance',root/'scripts/performance'] for p in base.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.suffix in ('.py','.json','.yml','.md','.c') or p.is_file() and p.name=='Dockerfile']
records=[]
for p in paths:
 r={'path':p.relative_to(root).as_posix(),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'source':p.read_text(encoding='utf-8-sig')}
 if p.suffix=='.py':
  tree=ast.parse(r['source']);r['functions']=[{'name':n.name,'line':n.lineno,'source':ast.get_source_segment(r['source'],n)} for n in ast.walk(tree) if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef))];r['top_level']=True
 records.append(r)
(evidence/'original-sources.json').write_text(json.dumps({'head':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'files':records},ensure_ascii=False,indent=2),encoding='utf-8')
new.mkdir(exist_ok=True)
for p in old.iterdir():
 if p.is_file():shutil.copy2(p,new/('PackCall.Test.csproj' if p.suffix=='.csproj' else p.name))
assert all((new/p.name).read_bytes()==p.read_bytes() for p in old.glob('*.cs'))
(evidence/'transfer-copy.json').write_text(json.dumps({'source_files':len(list(old.glob('*.cs'))),'byte_identical':True,'old_project_exists':old.exists()},indent=2))
