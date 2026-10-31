"""Session-owned resources and atomic files; no implicit use of the development project."""
import hashlib
import json
import os
from pathlib import Path
import uuid
from collectors import command, CollectionError

REPO = Path(__file__).resolve().parents[2]


def atomic(path, document):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    temporary.write_text(json.dumps(document, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def digest(document):
    return hashlib.sha256(json.dumps(document, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def finalize_artifacts(directory):
    from contract import bounds,validate
    directory=Path(directory)
    definitions={}
    for path in sorted(directory.rglob("*.jsonl")):
        with path.open(encoding="utf-8") as stream:
            for line in stream:
                if not line.endswith("\n"): break
                row=json.loads(line)
                for name,metric in row.get("metrics",{}).items():
                    definitions[name]=dict(unit=metric["unit"],kind=metric["kind"],
                        description=name+"; operation duration belongs to its completion window; counters record observed confirmations, not inferred commits." if metric["kind"]=="histogram" or name.startswith("terminal.") else name+"; missing values are unknown, not zero; resource samples have their own collection boundaries.",
                        reset="stats_reset" if name.startswith("postgres.") and metric["kind"]=="cumulative" else "process_instance" if metric["kind"] in {"delta","cumulative"} else "none",
                        denominator="one_core" if metric["unit"]=="cores" else None)
    schema=dict(schema_version=1,histogram_bounds_ms=bounds(),percentile_method="nearest_rank",definitions=definitions)
    if definitions:
        validate("metric-definitions",schema)
        atomic(directory/"metrics-schema.json",schema)
    checksums={}
    for path in sorted(directory.rglob("*")):
        if path.is_file() and path.name not in {"checksums.json",".byte-budget","summary.json","cleanup.json","stop-request.json"} and path.suffix in {".json",".jsonl"}:
            with path.open("rb") as stream:checksums[str(path.relative_to(directory)).replace("\\","/")]=hashlib.file_digest(stream,"sha256").hexdigest()
    atomic(directory/"checksums.json",{"schema_version":1,"files":checksums})


def load_owned(directory):
    directory = Path(directory).resolve()
    state = json.loads((directory / "session.json").read_text())
    sid = str(uuid.UUID(state["session_id"]))
    if directory.name != sid or state["project"] != "packcall-perf-" + sid.replace("-", ""):
        raise CollectionError("invalid_session_ownership")
    for container in state.get("containers", {}).values():
        label = command(["docker", "inspect", "--format", '{{index .Config.Labels "packcall.performance.session"}}', container]).strip()
        if label != sid: raise CollectionError("invalid_resource_ownership")
    return directory, state


def stop_owned(directory):
    directory, state = load_owned(directory)
    atomic(directory / "stop-request.json", {"session_id": state["session_id"], "reason": "operator_requested"})
    workers = [container for role, container in state["containers"].items() if role.startswith("worker")]
    if workers: command(["docker", "stop", "--time", "35", *workers], timeout=45)


def cleanup_owned(directory):
    directory, state = load_owned(directory)
    sid = state["session_id"]
    volume = state["volume"]
    label = command(["docker", "volume", "inspect", "--format", '{{index .Labels "packcall.performance.session"}}', volume]).strip()
    if label != sid: raise CollectionError("invalid_volume_ownership")
    containers = list(state["containers"].values())
    if containers: command(["docker", "rm", "-f", *containers], timeout=60)
    command(["docker", "volume", "rm", volume])
    # Artifacts remain available for offline analysis.
    atomic(directory / "cleanup.json", {"session_id": sid, "resources_removed": True})


def compose_profile(session_id, output, workers=1, enabled=True):
    labels = {"packcall.performance.session": session_id}
    common = {"PACKCALL_ENVIRONMENT": "test", "TEST_SENDER_MODE": "http", "TEST_SIMULATE_ERRORS": "false",
              "TEST_OPERATOR_URL": "http://http:8000/", "TEST_OPERATOR_TIMEOUT_SECONDS": "5",
              "ConnectionStrings__Postgres": "Host=db;Database=performance;Username=postgres;Password=${PERF_PASSWORD}"}
    diagnostic = {"PerformanceMeasurement__Enabled": str(enabled).lower(), "PerformanceMeasurement__SessionId": session_id,
                  "PerformanceMeasurement__OutputDirectory": "/measurement", "PerformanceMeasurement__Interval": "00:00:10"}
    services = {
        "db": {"image": "postgres:18.6-alpine3.24", "labels": labels, "environment": {"POSTGRES_DB": "performance", "POSTGRES_PASSWORD": "${PERF_PASSWORD}"},
               "volumes": ["data:/var/lib/postgresql"], "healthcheck": {"test": ["CMD", "pg_isready", "-U", "postgres", "-d", "performance"], "interval": "1s", "timeout": "3s", "retries": 60}},
        "http": {"build": {"context": str(REPO / "src/PackCall.TestEmailOperator")}, "labels": labels},
    }
    for role, project in [("api", "Api"), ("migrator", "Migrator")] + [("worker" + str(i), "Worker") for i in range(workers)]:
        environment = dict(common)
        if project != "Migrator": environment.update(diagnostic)
        if project == "Worker":
            environment.update({"DeliveryWorker__Parallelism": "4", "DeliveryWorker__BatchSize": "10", "DeliveryWorker__LockDuration": "00:01:30",
                                "DeliveryWorker__RecoveryInterval": "00:00:15", "DeliveryWorker__IdleDelay": "00:00:00.250", "DeliveryWorker__ErrorDelay": "00:00:05"})
        service = dict(build={"context": str(REPO), "dockerfile": f"src/PackCall.{project}/Dockerfile"}, environment=environment,
                       labels=labels, restart="no", stop_grace_period="40s")
        if project != "Migrator": service["volumes"] = [str(Path(output).resolve()) + ":/measurement"]
        if project == "Api": service["ports"] = ["127.0.0.1::8080"]
        services[role] = service
    return {"services": services, "volumes": {"data": {"labels": labels}}}
