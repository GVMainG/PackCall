import subprocess as sp,json
from pathlib import Path
r={}
for app in ['Api','Worker','Migrator']:
 image='packcall'+app.lower()
 shared=sp.run(['docker','run','--rm','--entrypoint','sh',image,'-c','test -f /app/appsettings.packcall.json'],capture_output=True)
 assert shared.returncode==0
 absent=sp.run(['docker','run','--rm','--user','0','--entrypoint','sh',image,'-c',f'mv /app/appsettings.packcall.json /app/shared.bak; cd /tmp; dotnet /app/PackCall.{app}.dll'],capture_output=True)
 assert absent.returncode!=0
 r[app]={'image':sp.check_output(['docker','image','inspect','-f','{{.Id}}',image],text=True).strip(),'sharedFile':True,'missingSharedExit':absent.returncode}
Path('_bmad-output/implementation-artifacts/story-2-1-worker/images.json').write_text(json.dumps(r,indent=2));print(json.dumps(r,indent=2))
