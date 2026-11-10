import subprocess as sp,json
from pathlib import Path
out=Path('_bmad-output/implementation-artifacts/story-2-1-worker')
out.mkdir(parents=True, exist_ok=True)
args=['docker','compose','-p','packcall-w21-failure','-f','docker-compose.yml','-f',str(Path(__file__).parent/'compose.failure.yml'),'up','-d']
r=sp.run(args,capture_output=True,text=True,encoding='utf-8');assert r.returncode!=0
states={}
for app in ['api','worker','migrator']:
 name='packcall-w21-failure-packcall.'+app+'-1'
 s=json.loads(sp.check_output(['docker','inspect',name],text=True,encoding='utf-8'))[0]['State'];states[app]={'status':s['Status'],'exit':s['ExitCode']}
assert states['api']['status']=='created' and states['worker']['status']=='created' and states['migrator']['exit']==1
(out/'compose-failure.json').write_text(json.dumps({'upExit':r.returncode,'states':states},indent=2));print(json.dumps(states,indent=2))
