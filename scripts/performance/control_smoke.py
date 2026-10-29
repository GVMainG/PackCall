"""Abort a fresh small session before API start, verify files/DB retained, then explicit owned cleanup."""
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid
from session import REPO,stop_owned,cleanup_owned

if __name__=="__main__":
    output=REPO/"artifacts"/("performance-control-"+uuid.uuid4().hex)
    process=subprocess.Popen([sys.executable,str(Path(__file__).with_name("run.py")),"--profile","small-http","--count","20","--output",str(output)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    deadline=time.monotonic()+180
    while time.monotonic()<deadline:
        sessions=list(output.glob("*/session.json"))
        if sessions:
            directory=sessions[0].parent
            state=json.loads(sessions[0].read_text())
            if "worker0" in state["containers"] and list(directory.glob("processes/*/process.json")):
                stop_owned(directory)
                break
        if process.poll() is not None: raise RuntimeError("control_fixture_ended_early")
        time.sleep(.1)
    else:
        process.terminate()
        raise RuntimeError("control_fixture_timeout")
    stdout,stderr=process.communicate(timeout=90)
    if process.returncode:raise RuntimeError("control_fixture_failed")
    manifest=json.loads((directory/"manifest.json").read_text())
    assert manifest["workload_status"]=="aborted"
    assert not (directory/"processing-epoch.json").exists()
    assert (directory/"initial.json").exists()
    cleanup_owned(directory)
    assert (directory/"manifest.json").exists()
    print("PASS: stop by SessionId before start, DB retained until explicit cleanup, artifacts preserved; "+str(directory))
