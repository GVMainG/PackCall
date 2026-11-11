"""Isolated HTTP performance session. Preparation is never counted as processing."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import uuid
from collectors import command, CollectionError, ScheduledCollector, queue_snapshot, postgres_snapshot, container_snapshot, host_snapshot, docker_inventory, collector_rss, utc
from session import REPO, atomic, digest, compose_profile, finalize_artifacts
from contract import ROOT, validate


def compose_output(arguments, *, environment=None, timeout=180):
    try:
        result = subprocess.run(["docker", "compose", *arguments], env=environment, capture_output=True,
                                text=True, encoding="utf-8", errors="replace", timeout=timeout)
    except (OSError, subprocess.TimeoutExpired):
        raise CollectionError("compose_command_failed") from None
    if result.returncode:
        raise CollectionError("compose_command_failed")
    return result.stdout


WORKER_STARTED = re.compile(
    r"Фоновый обработчик доставок запущен\. WorkerId=([0-9a-f]{12}-[0-9a-f]{32}), Parallelism=(\d+), BatchSize=(\d+)$",
    re.MULTILINE,
)
CANARY_EMAIL = re.compile(rb"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}")


def verify_privacy(output, secret=None):
    for path in output.rglob("*"):
        if not path.is_file() or path.name == ".byte-budget":
            continue
        if path.suffix not in {".json", ".jsonl"}:
            raise CollectionError("unexpected_artifact")
        with path.open("rb") as stream:
            for row in stream:
                if (CANARY_EMAIL.search(row) or b"x" * 64 in row or
                    (secret and secret in row) or b"ConnectionStrings__" in row or
                    b"Password=" in row or b"Traceback (most recent call last)" in row):
                    raise CollectionError("privacy_canary_detected")


def run(args, fault=None, *, product_root=REPO, enabled=True, images=None, latency_probe=None,
        protocol=None):
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
    state = {"schema_version": 1, "session_id": sid, "project": project,
             "volume": project + "_data", "containers": {}}
    atomic(output / "session.json", state)
    settings = dict(parallelism=4, batch_size=10, lock_duration_s=90, recovery_interval_s=15, idle_delay_s=.25,
                    error_delay_s=5, http_timeout_s=5, simulate_errors=False, operator_mode="http", environment="test",
                    interval_s=10, process_sample_s=5, queue_sample_s=30)
    manifest = json.loads((ROOT / "examples/manifest.json").read_text())
    manifest.update(session_id=sid, profile=args.profile, effective_settings=settings, settings_sha256=digest(settings),
                    start_utc=utc(), participants=[], planned_roles=["api", "worker", "driver", "postgres", "http"])
    manifest["load"].update(deliveries=args.count, message_bytes=64, email_bytes=len("perf000000001@example.invalid"))
    manifest["commit"] = command(["git", "-C", str(product_root), "rev-parse", "HEAD"]).strip()
    manifest["dirty"] = bool(command(["git", "-C", str(product_root), "status", "--porcelain"]).strip())
    source = hashlib.sha256()
    for path in sorted((product_root / "src").rglob("*")):
        if path.is_file() and not {"bin", "obj"}.intersection(path.parts):
            source.update(str(path.relative_to(product_root)).encode()); source.update(path.read_bytes())
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
    collector_cpu_s = 0.0
    collection_stop = threading.Event()
    collector_failure = threading.Event()
    collector_thread = None
    def collect():
        nonlocal collector_cpu_s
        cpu_start = time.thread_time()
        # Leave 64 MiB for bounded worker shutdown buffers and final metadata.
        used = 0
        for path in output.rglob("*"):
            try:
                if path.is_file():
                    used += path.stat().st_size
            except FileNotFoundError:
                # Worker writers can replace a temporary file while it is being enumerated.
                continue
        if used > 2 * 1024**3 - 64 * 1024**2:
            raise CollectionError("session_output_limit")
        for collector in collectors:
            record = collector.tick()
            if record is not None:
                validate("sample",record)
                folder = output / "samples"; folder.mkdir(exist_ok=True)
                with (folder / (collector.source + ".jsonl")).open("a",encoding="utf-8") as stream: stream.write(json.dumps(record)+"\n")
        collector_cpu_s += time.thread_time() - cpu_start
    def collect_until_stopped():
        try:
            while not collection_stop.is_set():
                collect()
                collection_stop.wait(.25)
        except Exception:
            collector_failure.set()
    if protocol is not None:
        atomic(output / "protocol.json", protocol)
    def sidecar(name, content):
        if protocol is not None:
            content = {"schema_version": 1, **content}
        atomic(output / name, content)
    with tempfile.TemporaryDirectory(prefix="packcall-performance-") as temporary:
        compose = Path(temporary) / "compose.json"
        profile = compose_profile(sid, output, args.workers, enabled, product_root=product_root,
                                  images=images, latency_probe=latency_probe)
        atomic(compose, profile)
        def dc(*arguments, timeout=180):
            return compose_output(["-p", project, "-f", str(compose), *arguments],
                                  environment=environment, timeout=timeout)
        def sql(text, **variables):
            argv=["docker","exec","-i",state["containers"]["db"],"psql","-U","postgres","-d","performance","-qAt","-v","ON_ERROR_STOP=1"]
            for key,value in variables.items(): argv.extend(["-v",key+"="+str(value)])
            return command(argv,input=text,timeout=180).strip()
        def snapshot():
            return json.loads(sql("SELECT json_build_object('campaign_status',(SELECT status FROM campaigns WHERE id=:'cid'::uuid),'campaigns',(SELECT count(*) FROM campaigns),'total',count(*),'waiting',count(*) FILTER(WHERE delivery_status=0),'in_progress',count(*) FILTER(WHERE delivery_status=1),'sent',count(*) FILTER(WHERE delivery_status=2),'failed',count(*) FILTER(WHERE delivery_status=-1)) FROM deliveries WHERE campaign_id=:'cid'::uuid;",cid=manifest["campaign_id"]))
        def clock_probe(stage):
            measurements={}
            for role,container in state["containers"].items():
                if role=="migrator" or (protocol is not None and role=="db"): continue
                before=time.time()
                try: remote=float(command(["docker","exec",container,"date","+%s.%N"]).strip())
                except CollectionError:
                    measurements[role]={"missing_reason":"source_lost"}
                    continue
                after=time.time()
                measurements[role]={"offset_s":remote-(before+after)/2,"uncertainty_s":(after-before)/2,"request_utc_s":before,"response_utc_s":after}
            before=time.time();remote=float(sql("SELECT extract(epoch FROM clock_timestamp());"));after=time.time()
            measurements["postgres"]={"offset_s":remote-(before+after)/2,"uncertainty_s":(after-before)/2,
                                      "request_utc_s":before,"response_utc_s":after}
            sidecar("clock-"+stage+".json", measurements)
        def remember():
            for role in profile["services"]:
                identifier=dc("ps","--all","-q",role).strip()
                if identifier: state["containers"][role]=identifier
            atomic(output/"session.json",state)
        try:
            if images is None:
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
            sidecar("initial.json", initial)
            if args.prepare_only:
                manifest.update(workload_status="aborted",stop_reason="aborted",stop_utc=utc())
                atomic(output/"manifest.json",manifest)
                print(str(output)); return
            worker_roles=["worker"+str(i) for i in range(args.workers)]
            dc("up","-d",*worker_roles)
            remember()
            inventory=docker_inventory({role:value for role,value in state["containers"].items() if role!="migrator"})
            sidecar("inventory.json", inventory)
            manifest["environment"]["backend"]=inventory["backend"]
            manifest["image_digests"]={role:item["image"] for role,item in inventory["containers"].items()}
            db=state["containers"]["db"]
            if enabled:
                collectors=[ScheduledCollector(sid,"queue",30,lambda:queue_snapshot(db,"performance",manifest["campaign_id"]),epoch),
                            ScheduledCollector(sid,"postgres",15,lambda:postgres_snapshot(db,"performance"),epoch),
                            ScheduledCollector(sid,"postgres",60,lambda:postgres_snapshot(db,"performance",tables=True),epoch),
                            ScheduledCollector(sid,"containers",5,lambda:container_snapshot({k:v for k,v in state["containers"].items() if k!="migrator"}),epoch),
                            ScheduledCollector(sid,"host",15,host_snapshot,epoch)]
                if protocol is None:
                    collect()
            if (output/"stop-request.json").exists(): raise KeyboardInterrupt
            deadline=time.monotonic()+60
            ready = {}
            warmup_until = None
            while True:
                if (output/"stop-request.json").exists(): raise KeyboardInterrupt
                if time.monotonic()>deadline: raise CollectionError("measurement_ready_timeout")
                if protocol is not None:
                    for role in worker_roles:
                        if role in ready: continue
                        logs = command(["docker", "logs", "--tail", "40", state["containers"][role]])
                        match = WORKER_STARTED.search(logs)
                        if match and tuple(map(int, match.groups()[1:])) == (settings["parallelism"], settings["batch_size"]):
                            ready[role] = match[1]
                workers_ready = protocol is None or len(ready) == args.workers
                diagnostics_ready = not enabled or len(list(output.glob("processes/*/intervals-*.jsonl"))) == args.workers + 1
                if workers_ready and warmup_until is None:
                    warmup_until = time.monotonic() + (11 if protocol is not None else 0)
                if workers_ready and diagnostics_ready and time.monotonic() >= warmup_until: break
                time.sleep(.2)
            if protocol is not None:
                atomic(output / "readiness.json", {"schema_version": 1, "workers": ready})
            if (output/"stop-request.json").exists(): raise KeyboardInterrupt
            for path in output.glob("processes/*/process.json"):
                participant=json.loads(path.read_text())
                if participant["source"]=="worker":
                    if protocol is not None and participant["worker_id"] not in ready.values():
                        raise CollectionError("worker_measurement_identity_mismatch")
                    actual=participant.get("effective_settings") or {}
                    mapping={"Parallelism":"parallelism","BatchSize":"batch_size","LockDurationSeconds":"lock_duration_s","RecoveryIntervalSeconds":"recovery_interval_s","IdleDelaySeconds":"idle_delay_s","ErrorDelaySeconds":"error_delay_s","HttpTimeoutSeconds":"http_timeout_s","SimulateErrors":"simulate_errors","OperatorMode":"operator_mode"}
                    if any(actual.get(key)!=settings[value] for key,value in mapping.items()): raise CollectionError("effective_settings_mismatch")
                manifest["participants"].append(dict(instance_id=participant["instance_id"],role=participant["source"],worker_id=participant["worker_id"],boot_utc=participant["boot_utc"]))
            if enabled or protocol is not None:
                clock_probe("initial")
            immutable={"settings":settings,"images":manifest["image_digests"],"load":manifest["load"],"workers":args.workers,"inventory":inventory,"fixture_fault":fault}
            if protocol is not None:
                immutable["schema_version"] = 1
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
            lifecycle("processing")
            processing={"request_start_utc":utc(),"elapsed_start_s":time.monotonic()-epoch}
            if enabled and protocol is not None:
                for collector in collectors:
                    collector.due = time.monotonic()
                collector_thread = threading.Thread(target=collect_until_stopped)
                collector_thread.start()
            started=api("campaigns/"+manifest["campaign_id"]+"/start",{})
            processing.update(response_end_utc=utc(),elapsed_end_s=time.monotonic()-epoch,business_started_at=started.get("startedAt"))
            sidecar("processing-epoch.json", processing)
            manifest["workload_status"]="running"; atomic(output/"manifest.json",manifest)
            progress=time.monotonic(); last=-1
            previous_poll = None
            next_poll = time.monotonic()
            while True:
                if digest(json.loads((output/"profile.json").read_text()))!=profile_hash: raise CollectionError("profile_changed")
                if collector_failure.is_set(): raise CollectionError("collector_failed")
                if enabled and protocol is None:
                    collect()
                request_s = time.monotonic() - epoch
                current=snapshot(); terminal=current["sent"]+current["failed"]
                response_s = time.monotonic() - epoch
                poll = {"request_s": request_s, "response_s": response_s, "sent": current["sent"],
                        "campaign_status": current["campaign_status"], "response_utc": utc()}
                if terminal!=last: last=terminal; progress=time.monotonic()
                if (output/"stop-request.json").exists(): raise KeyboardInterrupt
                if (current["sent"] if protocol is not None else terminal)==args.count and current["campaign_status"]==2:
                    if protocol is not None:
                        atomic(output / "completion.json", {"schema_version": 1, "previous": previous_poll,
                                                           "confirmed": poll})
                    lifecycle("draining"); break
                previous_poll = poll
                if time.monotonic()-progress>120:
                    manifest["workload_status"]="completion_stalled" if terminal==args.count else "stalled"
                    lifecycle(manifest["workload_status"]); break
                if protocol is None:
                    time.sleep(1)
                else:
                    next_poll = max(next_poll + 1, time.monotonic())
                    time.sleep(max(0, next_poll-time.monotonic()))
            collection_stop.set()
            if collector_thread is not None:
                collector_thread.join()
            if collector_failure.is_set(): raise CollectionError("collector_failed")
            if protocol is not None and enabled:
                atomic(output / "collector-cost.json", {"schema_version": 1, "cpu_s": collector_cpu_s,
                       "peak_working_set_bytes": collector_rss(peak=True), "child_process_cpu_included": False})
            if enabled or protocol is not None:
                clock_probe("final")
            dc("stop","--timeout","35",*worker_roles,timeout=60)
            lifecycle("process_stopped")
            final=snapshot(); sidecar("final.json", final)
            journal=json.loads(sql("SELECT json_build_object('capture_entries',count(*) FILTER(WHERE previous_status=0 AND new_status=1),'terminal_entries',count(*) FILTER(WHERE new_status IN(-1,2)),'release_entries',count(*) FILTER(WHERE previous_status=1 AND new_status=0)) FROM delivery_status_journal WHERE campaign_id=:'cid'::uuid;",cid=manifest["campaign_id"]))
            sidecar("final-journal.json", journal)
            repeat_sql = (ROOT/"sql/repeat_captures.sql").read_text()
            if protocol is not None:
                repeat_sql = repeat_sql.replace("'500ms'", "'30s'")
            repeat=json.loads(sql(repeat_sql,campaign_id=manifest["campaign_id"]))
            sidecar("repeat-captures.json", repeat)
            if protocol is not None:
                rows = json.loads(sql("""
                    SELECT json_agg(row_to_json(per_worker) ORDER BY worker_id) FROM (
                        SELECT worker_id, count(*) AS sent, count(DISTINCT delivery_id) AS unique_sent
                        FROM delivery_status_journal WHERE campaign_id=:'cid'::uuid AND new_status=2
                        GROUP BY worker_id
                    ) per_worker;
                    """, cid=manifest["campaign_id"]))
                if not isinstance(rows, list) or any(row["worker_id"] not in ready.values() for row in rows):
                    raise CollectionError("worker_journal_identity_mismatch")
                containers = docker_inventory({role: state["containers"][role] for role in worker_roles})["containers"]
                workers = {role: {"sent": row["sent"], "unique_sent": row["unique_sent"],
                                  "restart_count": containers[role]["restart_count"], "oom": containers[role]["oom"],
                                  "exit_code": int(command(["docker", "inspect", "--format", "{{.State.ExitCode}}",
                                                            state["containers"][role]]))}
                           for role, worker_id in ready.items() for row in rows if row["worker_id"] == worker_id}
                unique_sent = int(sql("""SELECT count(DISTINCT delivery_id) FROM delivery_status_journal
                    WHERE campaign_id=:'cid'::uuid AND new_status=2;""", cid=manifest["campaign_id"]))
                atomic(output/"worker-journal.json", {"schema_version": 1, "workers": workers, "unique_sent": unique_sent})
            if final["sent"]+final["failed"]==args.count and final["campaign_status"]==2:
                manifest["workload_status"]="completed"; lifecycle("completed")
        except KeyboardInterrupt:
            manifest["workload_status"]="aborted"; lifecycle("aborted")
        except Exception as error:
            manifest["workload_status"]="aborted"; manifest["telemetry_status"]="failed"
            code = str(error) if isinstance(error, CollectionError) and re.fullmatch(r"[a-z_]+", str(error)) else "session_failed"
            atomic(output/"failure.json",{"schema_version": 1, "code":code})
            lifecycle("collector_failure")
            raise CollectionError(code) from None
        finally:
            collection_stop.set()
            if collector_thread is not None:
                collector_thread.join()
            stop_failed = False
            try:
                if protocol is not None:
                    remember()
                dc("stop","--timeout","35",timeout=90)
            except CollectionError:
                stop_failed = True
                manifest.update(workload_status="aborted", telemetry_status="failed")
                if not (output / "failure.json").exists():
                    atomic(output/"failure.json", {"schema_version": 1, "code": "compose_stop_failed"})
            manifest.update(stop_utc=utc(),stop_reason=manifest["workload_status"] if manifest["workload_status"] in {"completed","aborted","stalled","completion_stalled"} else "aborted")
            markers=[json.loads(path.read_text()) for path in output.glob("processes/*/stopped.json")]
            if manifest["telemetry_status"]!="failed":
                complete = not markers if not enabled else len(markers)==args.workers+1 and all(
                    row["telemetry_status"]=="complete" for row in markers)
                manifest["telemetry_status"]="complete" if complete else "partial"
            atomic(output/"manifest.json",manifest)
            finalize_artifacts(output)
            if protocol is not None:
                verify_privacy(output, environment["PERF_PASSWORD"].encode())
            if stop_failed and protocol is None and sys.exc_info()[0] is None:
                raise CollectionError("compose_stop_failed")
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
