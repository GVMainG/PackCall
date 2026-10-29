"""Run all existing .NET tests against a new, localhost-only PostgreSQL fixture."""
import os
from pathlib import Path
import subprocess
import time
import uuid
from collectors import command,CollectionError
from session import REPO,atomic

if __name__=="__main__":
    sid=str(uuid.uuid4());name="packcall-perf-tests-"+sid
    output=REPO/"artifacts/performance"/sid
    output.mkdir(parents=True)
    command(["docker","run","-d","--name",name,"-p","127.0.0.1::5432","-e","POSTGRES_HOST_AUTH_METHOD=trust","postgres:18.6-alpine3.24"])
    try:
        for _ in range(60):
            try: command(["docker","exec",name,"pg_isready","-U","postgres"]);break
            except CollectionError:time.sleep(.5)
        port=command(["docker","port",name,"5432"]).strip().rsplit(":",1)[1]
        environment=dict(os.environ,PACKCALL_TEST_POSTGRES=f"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres")
        result=subprocess.run(["dotnet","test",str(REPO/"PackCall.Core.Test/PackCall.Core.Test.csproj"),"--verbosity","quiet","--logger","trx","--results-directory",str(output)],env=environment,capture_output=True,text=True,timeout=600)
        atomic(output/"test-result.json",{"exit_code":result.returncode,"isolated":True})
        print(result.stdout[-2500:])
        if result.returncode: raise RuntimeError("postgres_test_failure")
        print(str(output))
    finally:command(["docker","rm","-f",name])
