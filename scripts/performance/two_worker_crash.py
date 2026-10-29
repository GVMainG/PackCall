from argparse import Namespace
import json
from run import run
from analyze import analyze

if __name__=="__main__":
    output=run(Namespace(output="artifacts/performance",profile="small-http",count=40,workers=2,prepare_only=False),fault="kill_worker_after_two_windows")
    summary=analyze(output)
    manifest=json.loads((output/"manifest.json").read_text())
    workers=[row for row in manifest["participants"] if row["role"]=="worker"]
    assert len({row["instance_id"] for row in workers})==2
    assert manifest["workload_status"]=="completed"
    assert manifest["telemetry_status"]=="partial"
    assert "process_without_stop_marker" in summary["quality"]
    assert summary["reconciliation_difference"]==0
    print("PASS: two worker instances, SIGKILL after two flushed windows, surviving worker completed 40 deliveries; telemetry partial")
