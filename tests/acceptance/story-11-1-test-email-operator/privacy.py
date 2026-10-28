import pathlib,subprocess as sp,socket,urllib.request,urllib.error,json,time
ROOT=pathlib.Path.cwd();OUT=ROOT/'_bmad-output/implementation-artifacts/story-11-1-test-email-operator'
python=str(OUT/'venv/Scripts/python.exe'); records=[]
for forced in [False,True]:
 with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
 code="import sys;sys.path.insert(0,'src/PackCall.TestEmailOperator');import app,uvicorn;"
 if forced:code+="app.decide=lambda payload: (_ for _ in ()).throw(RuntimeError('private-exception-marker'));"
 code+=f"uvicorn.run(app.app,host='127.0.0.1',port={port},access_log=False,log_level='critical')"
 process=sp.Popen([python,'-c',code],stdout=sp.PIPE,stderr=sp.PIPE,text=True)
 try:
  for i in range(100):
   try:urllib.request.urlopen(f'http://127.0.0.1:{port}/healthz',timeout=.2).close();break
   except Exception:time.sleep(.05)
  else:raise AssertionError('startup')
  payload=dict(delivery_id='11111111-1111-1111-1111-111111111111',campaign_id='22222222-2222-2222-2222-222222222222',recipient_id='33333333-3333-3333-3333-333333333333',recipient_email='privacy-email-marker@example.test',message_text='privacy-text-marker',simulate_errors=False)
  responses=[]
  for body in [payload,dict(payload,recipient_email='invalid-email-marker')]:
   request=urllib.request.Request(f'http://127.0.0.1:{port}/v1/email/send?query=privacy-query-marker',data=json.dumps(body).encode(),headers={'Content-Type':'application/json'})
   try:
    with urllib.request.urlopen(request,timeout=3) as r:responses.append({'status':r.status,'body':r.read().decode()})
   except urllib.error.HTTPError as r:responses.append({'status':r.code,'body':r.read().decode()})
  assert [x['status'] for x in responses]==([500,500] if forced else [200,422])
 finally:
  process.terminate();stdout,stderr=process.communicate(timeout=10)
 assert not stdout.strip() and not stderr.strip(),(stdout,stderr)
 records.append(dict(forced=forced,responses=responses,stdout=stdout,stderr=stderr))
(OUT/'privacy-python.json').write_text(json.dumps(records,indent=2));print('Uvicorn privacy success/validation/forced-exception PASS')
