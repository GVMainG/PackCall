import pathlib,json,subprocess as sp
OUT=pathlib.Path('_bmad-output/implementation-artifacts/story-11-1-test-email-operator');RUN=pathlib.Path((OUT/'latest-run.txt').read_text())
if json.loads((RUN/'results.json').read_text()).get('status')!='passed':
 raise SystemExit('Refusing retention checks: latest run must pass')
entries=[json.loads(x) for x in (RUN/'commands.jsonl').read_text(encoding='utf-8').splitlines()]
project=next(x['args'][x['args'].index('-p')+1] for x in entries if '-p' in x['args'] and any('packcall-b11-http-' in a for a in x['args']))
name=project+'-packcall.test-email-operator-1'
def cmd(*args,input=None,capture=False):
 r=sp.run(['docker',*args],input=input,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=35)
 assert r.returncode==0,r.stderr
 return r if capture else r.stdout
cmd('start',name)
try:
 import time
 for _ in range(100):
  if json.loads(cmd('inspect',name))[0]['State']['Health']['Status']=='healthy':break
  time.sleep(.1)
 before=cmd('diff',name)
 probe="""import json,urllib.request,urllib.error
p=dict(delivery_id='11111111-1111-1111-1111-111111111111',campaign_id='22222222-2222-2222-2222-222222222222',recipient_id='33333333-3333-3333-3333-333333333333',recipient_email='privacy-marker@example.test',message_text='privacy-marker-text',simulate_errors=False)
for payload in [p,p,dict(p,simulate_errors=True),dict(p,recipient_email='privacy-invalid-marker'),dict(p,simulate_errors='true')]:
 request=urllib.request.Request('http://localhost:8000/v1/email/send?marker=privacy-query-marker',data=json.dumps(payload).encode(),headers={'Content-Type':'application/json'})
 try:
  with urllib.request.urlopen(request) as r:assert r.status==200
 except urllib.error.HTTPError as r:assert r.code==422
"""
 assert cmd('exec','-i',name,'python','-',input=probe)==''
 after=cmd('diff',name);assert before==after
 cmd('restart',name)
 logs=cmd('logs',name,capture=True)
 (OUT/'retention-logs.json').write_text(json.dumps(dict(stdout=logs.stdout,stderr=logs.stderr)))
 assert logs.stdout.strip()=='' and logs.stderr.strip()==''
 inspect=json.loads(cmd('inspect',name))[0];assert inspect['Mounts']==[] and inspect['HostConfig']['ReadonlyRootfs']
 (OUT/'retention.json').write_text(json.dumps(dict(status='passed',before=before,after=after,stdout=logs.stdout,stderr=logs.stderr,mounts=inspect['Mounts'],image=inspect['Image']),indent=2))
 print('Docker repeat/restart/writable-layer/privacy PASS')
finally:cmd('stop',name)
