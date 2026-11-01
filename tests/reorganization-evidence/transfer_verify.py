from pathlib import Path
import json,xml.etree.ElementTree as ET
root=Path.cwd(); e=root/'tests/reorganization-evidence'
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def cases(p):
 tree=ET.parse(p);return sorted((n.attrib['testName'],n.attrib['outcome']) for n in tree.findall('.//t:UnitTestResult',ns))
a=cases(e/'baseline/baseline.trx');b=cases(e/'transfer/transfer.trx');assert a==b
callers=['PackCall.slnx','scripts/performance/postgres_tests.py','tests/acceptance/story-2-2-workers/verify.py','tests/acceptance/story-2-2-workers/README.md','tests/acceptance/story-11-1-test-email-operator/run.py']
for name in callers:
 p=root/name;p.write_bytes(p.read_bytes().replace(b'PackCall.Core.Test',b'PackCall.Test'))
assert (root/'PackCall.Core.Test').exists()
(e/'transfer-comparison.json').write_text(json.dumps({'identities_and_outcomes_equal':True,'records':len(a),'callers_updated_before_deletion':callers,'source_hashes':'transfer-copy.json','old_project_still_exists':True},indent=2))
