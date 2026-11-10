import subprocess as sp

# Required host env for direct `docker run` of the host/probe images: the images
# parse TestOperatorSettings and fail without a valid test mode (RUN-2).
HOST_ENV = (
    '-e', 'PACKCALL_ENVIRONMENT=test',
    '-e', 'TEST_SENDER_MODE=inprocess',
    '-e', 'TEST_SIMULATE_ERRORS=false',
)

def docker(*a,check=True):
 r=sp.run(['docker',*a],text=True,encoding="utf-8",capture_output=True)
 if check and r.returncode:raise RuntimeError(r.stderr)
 return r.stdout.strip()
