from argparse import Namespace
import json
from run import run
from analyze import analyze

if __name__=="__main__":
    output=run(Namespace(output="artifacts/performance",profile="small-http",count=1,workers=1,prepare_only=False),fault="terminal_journal_failure")
    summary=analyze(output)
    manifest=json.loads((output/"manifest.json").read_text())
    final=json.loads((output/"final.json").read_text())
    assert final["sent"]==1 and final["campaign_status"]==1
    assert manifest["workload_status"]=="completion_stalled"
    assert summary["reconciliation_difference"]==0
    assert json.loads((output/"final-journal.json").read_text())["terminal_entries"]==0
    print("PASS: terminal commit + journal failure, no false completion, 120s completion_stalled, no automatic repair")
