"""Isolated HTTP performance session. Preparation is never counted as processing."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid
from collectors import command, CollectionError, ScheduledCollector, queue_snapshot, postgres_snapshot, container_snapshot, host_snapshot, docker_inventory, utc
from session import REPO, atomic, digest, compose_profile,finalize_artifacts
from contract import ROOT, validate, bounds


def run(args, fault=None):
    if os.name != "nt": raise CollectionError("windows_profile_required")
    sid = str(uuid.uuid4())
    project = "packcall-perf-" + sid.replace("-", "")
    output = Path(args.output).resolve() / sid
    output.mkdir(parents=True)
    if shutil.disk_usage(output).free < 4 * 1024**3: raise CollectionError("insufficient_disk")
    probe = output / ".write-probe"
    probe.write_bytes(b"probe")
    probe.unlink()
    epoch = time.monotonic()
    state = {"session_id": sid, "project": project, "volume": project + "_data", "containers": {}}
    atomic(output / "session.json", state)
    settings = dict(parallelism=4, batch_size=10, lock_duration_s=90, recovery_interval_s=15, idle_delay_s=.25,
                    error_delay_s=5, http_timeout_s=5, simulate_errors=False, operator_mode="http", environment="test",
                    interval_s=10, process_sample_s=5, queue_sample_s=30)
    manifest = json.loads((ROOT / "examples/manifest.json").read_text())
    manifest.update(session_id=sid, profile=args.profile, effective_settings=settings, settings_sha256=digest(settings),
                    start_utc=utc(), participants=[], planned_roles=["api", "worker", "driver", "postgres", "http"])
    manifest["load"].update(deliveries=args.count, message_bytes=64, email_bytes=29)
    manifest["commit"] = command(["git", "-C", str(REPO), "rev-parse", "HEAD"]).strip()
    manifest["dirty"] = bool(command(["git", "-C", str(REPO), "status", "--porcelain"]).strip())
    source = hashlib.sha256()
    for path in sorted((REPO / "src").rglob("*")):
        if path.is_file() and not {"bin", "obj"}.intersection(path.parts):
            source.update(str(path.relative_to(REPO)).encode()); source.update(path.read_bytes())
    manifest["source_sha256"] = source.hexdigest()
    manifest["tool_versions"] = {"python":sys.version.split()[0], "docker":command(["docker","version","--format","{{.Server.Version}}"]).strip(),
                                 "dotnet_sdk":command(["dotnet","--version"]).strip()}
    driver_instance = str(uuid.uuid4())
    sequence = 0
    def lifecycle(event):
        nonlocal sequence
        now = utc(); elapsed = time.monotonic() - epoch
        record = dict(schema_version=1,session_id=sid,source="driver",instance_id=driver_instance,sequence=sequence,
                      start_utc=now,end_utc=now,duration_s=0,elapsed_start_s=elapsed,elapsed_end_s=elapsed,quality="complete",metrics={},event=event)
        sequence += 1
        with (output / "lifecycle.jsonl").open("a",encoding="utf-8") as stream: stream.write(json.dumps(record)+"\n")
    lifecycle("preparing")
    environment = dict(os.environ, PERF_PASSWORD=secrets.token_hex(24))
    collectors = []
    def collect():
        # Leave 64 MiB for bounded worker shutdown buffers and final metadata.
        if sum(path.stat().st_size for path in output.rglob("*") if path.is_file())>2*1024**3-64*1024**2:
            raise CollectionError("session_output_limit")
        for collector in collectors:
            record = collector.tick()
            if record is not None:
                validate("sample",record)
                folder = output / "samples"; folder.mkdir(exist_ok=True)
                with (folder / (collector.source + ".jsonl")).open("a",encoding="utf-8") as stream: stream.write(json.dumps(record)+"\n")
    with tempfile.TemporaryDirectory(prefix="packcall-performance-") as temporary:
        compose = Path(temporary) / "compose.json"
        atomic(compose, compose_profile(sid, output, args.workers))
        def dc(*arguments, timeout=180):
            result = subprocess.run(["docker","compose","-p",project,"-f",str(compose),*arguments], env=environment, capture_output=True, text=True, timeout=timeout)
            if result.returncode: raise CollectionError("compose_command_failed")
            return result.stdout
        def sql(text, **variables):
            argv=["docker","exec","-i",state["containers"]["db"],"psql","-U","postgres","-d","performance","-qAt","-v","ON_ERROR_STOP=1"]
            for key,value in variables.items(): argv.extend(["-v",key+"="+str(value)])
            return command(argv,input=text,timeout=180).strip()
        def snapshot():
            return json.loads(sql("SELECT json_build_object('campaign_status',(SELECT status FROM campaigns WHERE id=:'cid'::uuid),'campaigns',(SELECT count(*) FROM campaigns),'total',count(*),'waiting',count(*) FILTER(WHERE delivery_status=0),'in_progress',count(*) FILTER(WHERE delivery_status=1),'sent',count(*) FILTER(WHERE delivery_status=2),'failed',count(*) FILTER(WHERE delivery_status=-1)) FROM deliveries WHERE campaign_id=:'cid'::uuid;",cid=manifest["campaign_id"]))
        def clock_probe(stage):
            measurements={}
            for role,container in state["containers"].items():
                if role=="migrator": continue
                before=time.time()
                try: remote=float(command(["docker","exec",container,"date","+%s.%N"]).strip())
                except CollectionError:
                    measurements[role]={"missing_reason":"source_lost"}
                    continue
                after=time.time()
                measurements[role]={"offset_s":remote-(before+after)/2,"uncertainty_s":(after-before)/2,"request_utc_s":before,"response_utc_s":after}
            before=time.time();remote=float(sql("SELECT extract(epoch FROM clock_timestamp());"));after=time.time()
            measurements["postgres"]={"offset_s":remote-(before+after)/2,"uncertainty_s":(after-before)/2}
            atomic(output/("clock-"+stage+".json"),measurements)
        def remember():
            for role in compose_profile(sid,output,args.workers)["services"]:
                identifier=dc("ps","--all","-q",role).strip()
                if identifier: state["containers"][role]=identifier
            atomic(output/"session.json",state)
        try:
            dc("build",timeout=1200)
            dc("up","-d","--wait","db","http")
            remember()
            dc("run","--rm","migrator")
            dc("up","-d","api")
            remember()
            port=dc("port","api","8080").strip().rsplit(":",1)[1]
            def api(path, payload=None):
                request=urllib.request.Request("http://127.0.0.1:"+port+"/api/"+path,
                    data=None if payload is None else json.dumps(payload).encode(),headers={"Content-Type":"application/json"})
                with urllib.request.urlopen(request,timeout=180) as response: return json.load(response)
            deadline=time.monotonic()+60
            while True:
                try: api("campaigns"); break
                except OSError:
                    if time.monotonic()>deadline: raise CollectionError("api_not_ready")
                    time.sleep(.25)
            sql((ROOT/"sql/seed.sql").read_text(),count=args.count)
            campaign=api("campaigns",{"name":"performance-synthetic","messageText":"x"*64})
            manifest["campaign_id"]=campaign["id"]
            initial=snapshot()
            if initial != dict(campaign_status=0,campaigns=1,total=args.count,waiting=args.count,in_progress=0,sent=0,failed=0): raise CollectionError("preparation_mismatch")
            atomic(output/"initial.json",initial)
            if args.prepare_only:
                manifest.update(workload_status="aborted",stop_reason="aborted",stop_utc=utc())
                atomic(output/"manifest.json",manifest)
                print(str(output)); return
            worker_roles=["worker"+str(i) for i in range(args.workers)]
            dc("up","-d",*worker_roles)
            remember()
            inventory=docker_inventory({role:value for role,value in state["containers"].items() if role!="migrator"})
            atomic(output/"inventory.json",inventory)
            manifest["environment"]["backend"]=inventory["backend"]
            manifest["image_digests"]={role:item["image"] for role,item in inventory["containers"].items()}
            db=state["containers"]["db"]
            collectors=[ScheduledCollector(sid,"queue",30,lambda:queue_snapshot(db,"performance",manifest["campaign_id"]),epoch),
                        ScheduledCollector(sid,"postgres",15,lambda:postgres_snapshot(db,"performance"),epoch),
                        ScheduledCollector(sid,"postgres",60,lambda:postgres_snapshot(db,"performance",tables=True),epoch),
                        ScheduledCollector(sid,"containers",5,lambda:container_snapshot({k:v for k,v in state["containers"].items() if k!="migrator"}),epoch),
                        ScheduledCollector(sid,"host",15,host_snapshot,epoch)]
            collect()
            if (output/"stop-request.json").exists(): raise KeyboardInterrupt
            deadline=time.monotonic()+60
            while len(list(output.glob("processes/*/intervals-*.jsonl"))) < args.workers+1:
                if (output/"stop-request.json").exists(): raise KeyboardInterrupt
                if time.monotonic()>deadline: raise CollectionError("measurement_ready_timeout")
                time.sleep(.2)
            if (output/"stop-request.json").exists(): raise KeyboardInterrupt
            for path in output.glob("processes/*/process.json"):
                participant=json.loads(path.read_text())
                if participant["source"]=="worker":
                    actual=participant.get("effective_settings") or {}
                    mapping={"Parallelism":"parallelism","BatchSize":"batch_size","LockDurationSeconds":"lock_duration_s","RecoveryIntervalSeconds":"recovery_interval_s","IdleDelaySeconds":"idle_delay_s","ErrorDelaySeconds":"error_delay_s","HttpTimeoutSeconds":"http_timeout_s","SimulateErrors":"simulate_errors","OperatorMode":"operator_mode"}
                    if any(actual.get(key)!=settings[value] for key,value in mapping.items()): raise CollectionError("effective_settings_mismatch")
                manifest["participants"].append(dict(instance_id=participant["instance_id"],role=participant["source"],worker_id=participant["worker_id"],boot_utc=participant["boot_utc"]))
            clock_probe("initial")
            immutable={"settings":settings,"images":manifest["image_digests"],"load":manifest["load"],"workers":args.workers,"inventory":inventory,"fixture_fault":fault}
            atomic(output/"profile.json",immutable)
            profile_hash=digest(immutable)
            validate("manifest",manifest); atomic(output/"manifest.json",manifest)
            lifecycle("ready")
            if (output/"stop-request.json").exists(): raise KeyboardInterrupt
            if fault=="terminal_journal_failure":
                sql("CREATE FUNCTION perf_fail_terminal_journal() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.new_status IN(-1,2) THEN RAISE EXCEPTION 'injected_terminal_journal_failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER perf_fail_terminal_journal BEFORE INSERT ON delivery_status_journal FOR EACH ROW EXECUTE FUNCTION perf_fail_terminal_journal();")
            if fault=="kill_worker_after_two_windows":
                if args.workers!=2: raise CollectionError("fault_requires_two_workers")
                deadline=time.monotonic()+30
                while any(sum(path.read_bytes().count(b"\n") for path in (output/"processes"/participant["instance_id"]).glob("intervals-*.jsonl"))<2 for participant in manifest["participants"] if participant["role"]=="worker"):
                    if time.monotonic()>deadline: raise CollectionError("fault_prefix_timeout")
                    time.sleep(.1)
                command(["docker","kill","--signal","KILL",state["containers"]["worker0"]])
                lifecycle("source_lost")
            processing={"request_start_utc":utc(),"elapsed_start_s":time.monotonic()-epoch}
            lifecycle("processing")
            started=api("campaigns/"+manifest["campaign_id"]+"/start",{})
            processing.update(response_end_utc=utc(),elapsed_end_s=time.monotonic()-epoch,business_started_at=started.get("startedAt"))
            atomic(output/"processing-epoch.json",processing)
            manifest["workload_status"]="running"; atomic(output/"manifest.json",manifest)
            progress=time.monotonic(); last=-1
            while True:
                if digest(json.loads((output/"profile.json").read_text()))!=profile_hash: raise CollectionError("profile_changed")
                collect()
                current=snapshot(); terminal=current["sent"]+current["failed"]
                if terminal!=last: last=terminal; progress=time.monotonic()
                if (output/"stop-request.json").exists(): raise KeyboardInterrupt
                if terminal==args.count and current["campaign_status"]==2:
                    lifecycle("draining"); break
                if time.monotonic()-progress>120:
                    manifest["workload_status"]="completion_stalled" if terminal==args.count else "stalled"
                    lifecycle(manifest["workload_status"]); break
                time.sleep(1)
            clock_probe("final")
            dc("stop","--timeout","35",*worker_roles,timeout=60)
            lifecycle("process_stopped")
            final=snapshot(); atomic(output/"final.json",final)
            journal=json.loads(sql("SELECT json_build_object('capture_entries',count(*) FILTER(WHERE previous_status=0 AND new_status=1),'terminal_entries',count(*) FILTER(WHERE new_status IN(-1,2)),'release_entries',count(*) FILTER(WHERE previous_status=1 AND new_status=0)) FROM delivery_status_journal WHERE campaign_id=:'cid'::uuid;",cid=manifest["campaign_id"]))
            atomic(output/"final-journal.json",journal)
            repeat=json.loads(sql((ROOT/"sql/repeat_captures.sql").read_text(),campaign_id=manifest["campaign_id"]))
            atomic(output/"repeat-captures.json",repeat)
            if final["sent"]+final["failed"]==args.count and final["campaign_status"]==2:
                manifest["workload_status"]="completed"; lifecycle("completed")
        except KeyboardInterrupt:
            manifest["workload_status"]="aborted"; lifecycle("aborted")
        except Exception as error:
            manifest["workload_status"]="aborted"; manifest["telemetry_status"]="failed"
            atomic(output/"failure.json",{"code":str(error) if isinstance(error,CollectionError) else type(error).__name__})
            lifecycle("collector_failure")
            raise
        finally:
            dc("stop","--timeout","35",timeout=90)
            manifest.update(stop_utc=utc(),stop_reason=manifest["workload_status"] if manifest["workload_status"] in {"completed","aborted","stalled","completion_stalled"} else "aborted")
            markers=[json.loads(path.read_text()) for path in output.glob("processes/*/stopped.json")]
            if manifest["telemetry_status"]!="failed": manifest["telemetry_status"]="complete" if len(markers)==args.workers+1 and all(row["telemetry_status"]=="complete" for row in markers) else "partial"
            atomic(output/"manifest.json",manifest)
            finalize_artifacts(output)
    print(str(output))
    return output


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--profile",choices=["windows-http-300k","small-http"],default="windows-http-300k")
    parser.add_argument("--count",type=int)
    parser.add_argument("--workers",type=int,choices=[1,2],default=1)
    parser.add_argument("--output",default="artifacts/performance")
    parser.add_argument("--prepare-only",action="store_true")
    args=parser.parse_args()
    args.count=args.count if args.count is not None else 300000 if args.profile=="windows-http-300k" else 30
    if args.count<1 or args.profile=="windows-http-300k" and args.count!=300000: parser.error("invalid count for profile")
    run(args)
