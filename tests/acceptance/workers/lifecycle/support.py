import subprocess as sp

def docker(*a,check=True):
 r=sp.run(['docker',*a],text=True,encoding="utf-8",capture_output=True)
 if check and r.returncode:raise RuntimeError(r.stderr)
 return r.stdout.strip()
