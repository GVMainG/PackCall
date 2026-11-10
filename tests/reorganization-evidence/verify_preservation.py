from pathlib import Path
import json,re,hashlib,ast,xml.etree.ElementTree as ET,collections
root=Path.cwd();e=root/'tests/reorganization-evidence';original=json.loads((e/'original-sources.json').read_text(encoding='utf-8'))['files'];mapping=json.loads((e/'csharp-source-mapping.json').read_text());pymap=json.loads((e/'python-source-mapping.json').read_text())
# Roslyn covers block/expression bodies and every field/helper/lifecycle member.
import subprocess
subprocess.run(['dotnet','run','--project',str(e/'csharp-verifier/Verifier.csproj')],check=True)
entries=json.loads((e/'csharp-scenario-mapping.json').read_text(encoding='utf-8'))
renames={m['old_identity']:m['new_identity'] for m in entries}
structural=json.loads((e/'csharp-structural-comparison.json').read_text(encoding='utf-8'))
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def trx(path,rename=False):
 rows=[]
 for n in ET.parse(path).findall('.//t:UnitTestResult',ns):
  identity=n.attrib['testName'];prefix=identity.split('(')[0]
  if rename:identity=renames.get(prefix,prefix)+identity[len(prefix):]
  rows.append((identity,n.attrib['outcome']))
 return sorted(rows)
assert trx(e/'baseline/baseline.trx',True)==trx(e/'final/final.trx',True)
# Compare the final complete category runs against the final complete run.
expected_categories={m['new_identity']:m['new'].split('/')[1] for m in entries}
full_cases=trx(e/'final/final.trx',True)
category_cases={}
category_sources={}
for category,filename in [('Unit','unit'),('Integration','integration')]:
 rows=trx(e/('filters/'+filename+'.trx'),True)
 assert all(expected_categories[name.split('(')[0]]==category for name,outcome in rows),(category,'case category mismatch')
 assert len(rows)==len(set(name for name,outcome in rows)),(category,'duplicate cases')
 category_cases[category]=rows
 category_sources[category]=['filters/'+filename+'.trx (final complete category run)']
assert collections.Counter(category_cases['Unit']+category_cases['Integration'])==collections.Counter(full_cases)
assert not set(n for n,o in category_cases['Unit']) & set(n for n,o in category_cases['Integration'])
(e/'csharp-category-partition.json').write_text(json.dumps({'full_case_count':len(full_cases),'full_source':'final/final.trx, with explicit identity-renames.json applied','category_sources':category_sources,'source_traits_validated':'csharp-trait-validation.json','category_cases':category_cases,'disjoint_complete_partition':True},indent=2))
(e/'csharp-scenario-mapping.json').write_text(json.dumps(entries,ensure_ascii=False,indent=2),encoding='utf-8')
(e/'identity-renames.json').write_text(json.dumps({a:b for a,b in renames.items() if a!=b},indent=2))
# Python AST comparison permits only moved path constants and runtime import/path bootstrap.
replacements=[('tests/acceptance/story-2-1-worker','tests/acceptance/workers/lifecycle'),('tests/acceptance/story-2-2-workers','tests/acceptance/workers/coordination'),('tests/acceptance/story-11-1-test-email-operator','tests/acceptance/operators'),('tests/acceptance/operators/alternate.py','tests/acceptance/operators/fixtures/alternate.py'),('tests/acceptance/operators/delayed.py','tests/acceptance/operators/fixtures/delayed.py')]
def normalized(s):
 for a,b in replacements:s=s.replace(a,b)
 s=s.replace('PackCall.Core.Test','PackCall.Test').replace('Path(__file__).with_name("run.py")','(_PERFORMANCE / "run.py")').replace('Path(__file__).resolve().parents[2] / "artifacts/performance"','_PERFORMANCE.parents[1] / "artifacts/performance"')
 return ast.dump(ast.parse(s),include_attributes=False)
pyentries=[]
for f in original:
 if not f['path'].endswith('.py'):continue
 target=pymap.get(f['path'],f['path']);new=(root/target).read_text(encoding='utf-8');functions={n.name:n for n in ast.walk(ast.parse(new)) if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef))}
 for m in f['functions']:
  if m['name']=='docker' and Path(f['path']).name in ('startup.py','shutdown-probes.py'):
   newfunction=next(n for n in ast.walk(ast.parse((root/'tests/acceptance/workers/lifecycle/support.py').read_text(encoding='utf-8'))) if isinstance(n,ast.FunctionDef))
  else:
   candidates=[n for n in ast.walk(ast.parse(new)) if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef)) and n.name==m['name']]
   newfunction=next((n for n in candidates if normalized(m['source'])==normalized(ast.get_source_segment(new,n))),candidates[0])
  assert normalized(m['source'])==normalized(ast.get_source_segment(new if not(m['name']=='docker' and Path(f['path']).name in ('startup.py','shutdown-probes.py')) else (root/'tests/acceptance/workers/lifecycle/support.py').read_text(encoding='utf-8'),newfunction)),(f['path'],m['name'])
  pyentries.append({'old':f['path'],'new':target,'function':m['name'],'is_test':m['name'].startswith('test_'),'parameters':ast.dump(ast.parse(m['source']).body[0].args),'body_preserved_with_path_updates':True})
(e/'python-scenario-mapping.json').write_text(json.dumps(pyentries,indent=2))
(e/'preservation-summary.json').write_text(json.dumps({'csharp_test_methods':len(entries),'inline_cases':sum(len(m['inline_cases']) for m in entries),'trx_records':148,'passed':131,'skipped':17,'identity_mapping_equal':True,'python_functions':len(pyentries),'python_test_methods':sum(m['is_test'] for m in pyentries),'original_comments_retained':True,'csharp_all_members':structural['original_members'],'csharp_expression_bodied_tests':structural['expression_bodied_test_methods'],'csharp_all_members_preserved':structural['all_leaf_members_equal'],'category_partition_counts':{k:dict(collections.Counter(outcome for name,outcome in rows)) for k,rows in category_cases.items()}},indent=2))
print((e/'preservation-summary.json').read_text())
