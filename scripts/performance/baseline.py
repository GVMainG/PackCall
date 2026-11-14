"""Run the pinned beta.2 two-Worker baseline and validate its local evidence."""
import argparse
import hashlib
from itertools import product
import json
import math
import os
from pathlib import Path
import shutil
from statistics import median
import subprocess
import tempfile
import uuid

from collectors import CollectionError, command, docker_inventory, host_snapshot, utc
from contract import read
from reader import read_prefix
from run import compose_output, run, verify_privacy
from session import REPO, atomic, cleanup_owned, compose_profile, digest


TAG = "v0.5.0-beta.2"
PRODUCT_SHA = "50a3ac33a6ba3cafa805bdda5b1646a5d45ffbd2"
THROUGHPUT_ORDER = (("off", "on"), ("on", "off"), ("off", "on"))
LATENCY_ORDER = (("off", "on"), ("on", "off"))
WORKERS = {"worker0", "worker1"}
MIB = 1024**2
EXTERNAL_CORE_METRICS = {
    "queue": ("queue.waiting_total", "queue.in_progress", "queue.sent", "queue.failed"),
    "postgres": ("postgres.xact_commit", "postgres.xact_rollback", "postgres.blocks_hit", "postgres.blocks_read"),
    "host": ("host.ram_total", "host.ram_available", "host.cpu"),
    "containers": tuple(f"container.{role}.{metric}" for role in sorted(WORKERS | {"api", "db", "http"})
                        for metric in ("cpu", "memory")),
}


def require(condition, code):
    if not condition:
        raise CollectionError(code)


def load(root, name):
    return json.loads((root / name).read_text(encoding="utf-8"))


def versioned(root, name, *fields):
    document = load(root, name)
    require(document.keys() == {"schema_version", *fields} and document["schema_version"] == 1,
            "invalid_sidecar")
    return document


def throughput(count, start, completion):
    previous, confirmed = completion["previous"], completion["confirmed"]
    require(previous is not None and (previous["sent"], previous["campaign_status"]) != (count, 2),
            "missing_incomplete_poll")
    require((confirmed["sent"], confirmed["campaign_status"]) == (count, 2) and
            start < previous["request_s"] <= previous["response_s"] <= confirmed["request_s"] <= confirmed["response_s"],
            "invalid_completion_bounds")
    duration = confirmed["response_s"] - start
    return {"duration_s": duration, "uncertainty_s": confirmed["response_s"] - previous["request_s"],
            "deliveries_per_s": count / duration, "upper_deliveries_per_s": count / (previous["request_s"] - start)}


def throughput_gate(pairs):
    require(len(pairs) == 3 and all(pair.keys() == {"off", "on"} for pair in pairs), "incomplete_throughput_series")
    losses, loss_low, loss_high = [], [], []
    off_low, off_high = [], []
    for pair in pairs:
        off, on = pair["off"], pair["on"]
        off_low.append(off["deliveries_per_s"])
        off_high.append(off["upper_deliveries_per_s"])
        losses.append(100 * (1 - on["deliveries_per_s"] / off["deliveries_per_s"]))
        loss_low.append(100 * (1 - on["upper_deliveries_per_s"] / off["deliveries_per_s"]))
        loss_high.append(100 * (1 - on["deliveries_per_s"] / off["upper_deliveries_per_s"]))
    def spread_pct(rates):
        return 100 * (max(rates) - min(rates)) / median(rates)
    spread = spread_pct(off_low)
    # With fixed rank order the spread is monotonic in each rate. At a rank
    # crossover two rates tie, and moving toward the larger spread reaches endpoints.
    spread_upper = max(map(spread_pct, product(*zip(off_low, off_high))))
    low, high = median(loss_low), median(loss_high)
    status = "pass"
    if spread_upper > 5 or low <= 5 < high:
        status = "inconclusive"
    elif low > 5:
        status = "over_budget"
    return {"status": status, "paired_loss_pct": losses, "median_loss_pct": median(losses),
            "median_loss_bounds_pct": [low, high], "off_spread_pct": spread, "off_spread_upper_pct": spread_upper}


def pooled_p95(root, workers, count):
    durations = []
    for role, worker in workers.items():
        probe = versioned(root, f"latency-{role}.json", "capacity", "count", "overflow", "invalid", "durations_ms")
        values = probe["durations_ms"]
        require(probe["capacity"] == 300_000 and probe["overflow"] == probe["invalid"] == 0 and
                probe["count"] == worker["sent"] == len(values) <= probe["capacity"] and
                all(type(value) in {int, float} and math.isfinite(value) and value >= 0 for value in values),
                "invalid_latency_probe")
        durations.extend(values)
    require(len(durations) == count, "incomplete_latency_probe")
    durations.sort()
    return durations[math.ceil(0.95 * count) - 1]


def terminal_counts(metrics, totals):
    for status in totals:
        delta = metrics.get(f"terminal.{status}.delta", {}).get("value")
        cumulative = metrics.get(f"terminal.{status}.cumulative", {}).get("value")
        require(type(delta) is int and type(cumulative) is int, "missing_terminal_counters")
        totals[status] += delta
        require(cumulative == totals[status], "terminal_counter_reset")


def validate_external(root, session_id, start, end):
    files = {path.stem: path for path in (root / "samples").glob("*.jsonl")}
    require(files.keys() == EXTERNAL_CORE_METRICS.keys(), "missing_external_samples")
    for source, path in files.items():
        samples = read_prefix([path], "sample", contiguous=False)
        records = samples["records"]
        require(records and set(samples["quality"]) <= {"gap", "missing"} and all(
            row["session_id"] == session_id and row["source"] == source for row in records),
            "invalid_external_samples")
        require(any(
            start <= row["elapsed_start_s"] <= end and row["quality"] in {"complete", "gap"} and all(
                row["metrics"].get(name, {}).get("value") is not None for name in EXTERNAL_CORE_METRICS[source]
            ) for row in records
        ), f"missing_processing_samples_{source}")


def validate_on(root, manifest, readiness, workers, start, end):
    session_id = manifest["session_id"]
    processes = list((root / "processes").iterdir())
    expected = {p["instance_id"]: p for p in manifest["participants"]}
    require(len(processes) == len(expected) == 3 and sorted(p["role"] for p in expected.values()) ==
            ["api", "worker", "worker"], "incomplete_processes")
    sources = set()
    for process in processes:
        participant = expected[process.name]
        identity = versioned(process, "process.json", "session_id", "instance_id", "source", "worker_id",
                             "boot_utc", "interval_s", "runtime_instance_id", "state", "effective_settings")
        stop = versioned(process, "stopped.json", "session_id", "instance_id", "end_utc", "state",
                         "telemetry_status", "dropped_intervals")
        require(identity["session_id"] == stop["session_id"] == session_id and
                identity["instance_id"] == stop["instance_id"] == process.name and
                identity["source"] == participant["role"] and identity["worker_id"] == participant["worker_id"] and
                identity["state"] == "ready" and stop["state"] == "process_stopped" and
                stop["telemetry_status"] == "complete" and stop["dropped_intervals"] == 0,
                "incomplete_process_markers")
        totals = {"sent": 0, "failed": 0}
        interval = read_prefix(process.glob("intervals-*.jsonl"))
        records = interval["records"]
        require(records and interval["quality"] == ["partial"] and records[-1]["quality"] == "partial" and
                all(row["quality"] == "complete" for row in records[:-1]), "invalid_interval_quality")
        for row in records:
            metrics = row["metrics"]
            require(row["session_id"] == session_id and row["instance_id"] == process.name and
                    row["source"] == participant["role"] and
                    all(metrics[name]["value"] == 0 for name in ("diagnostics.dropped_measurements.delta",
                                                                  "diagnostics.dropped_intervals.cumulative")),
                    "measurement_loss")
            terminal_counts(metrics, totals)
        role = next((role for role, worker in readiness.items() if worker == identity["worker_id"]), "api")
        require(role not in sources and totals["failed"] == 0 and
                totals["sent"] == (workers[role]["sent"] if role in WORKERS else 0),
                "terminal_counter_mismatch")
        sources.add(role)
        runtime = read_prefix(process.glob("runtime-*.jsonl"), "sample", contiguous=False)
        require(runtime["records"] and not runtime["quality"] and all(
            row["session_id"] == session_id and row["source"] == "runtime" and
            row["process_instance_id"] == process.name and row["instance_id"] == identity["runtime_instance_id"]
            for row in runtime["records"]), "invalid_runtime_samples")
    require(sources == WORKERS | {"api"}, "invalid_telemetry_roles")
    validate_external(root, session_id, start, end)
    read("metric-definitions", root / "metrics-schema.json")


def verify_checksums(root):
    checksums = versioned(root, "checksums.json", "files")["files"]
    expected = {path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file() and
                path not in {root / "checksums.json", root / "cleanup.json"} and path.name != ".byte-budget"}
    require(expected == checksums.keys(), "incomplete_checksums")
    for name, checksum in checksums.items():
        require(sha256(root / name) == checksum, "checksum_mismatch")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def assess(root, count, protocol, images, comparison=None):
    manifest = read("manifest", root / "manifest.json")
    require(manifest["commit"] == PRODUCT_SHA and not manifest["dirty"] and
            manifest["workload_status"] == "completed" and manifest["telemetry_status"] == "complete" and
            manifest["load"]["deliveries"] == count, "incomplete_session")
    verify_privacy(root)
    verify_checksums(root)
    require(load(root, "protocol.json") == protocol, "protocol_mismatch")
    lifecycle = read_prefix([root / "lifecycle.jsonl"], "lifecycle", contiguous=False)
    require(not lifecycle["quality"] and [row["event"] for row in lifecycle["records"]] ==
            ["preparing", "ready", "processing", "draining", "process_stopped", "completed"] and
            all(row["session_id"] == manifest["session_id"] for row in lifecycle["records"]), "invalid_lifecycle")
    snapshot_fields = ("campaign_status", "campaigns", "total", "waiting", "in_progress", "sent", "failed")
    initial, final = (versioned(root, f"{stage}.json", *snapshot_fields) for stage in ("initial", "final"))
    require(tuple(initial[field] for field in snapshot_fields) == (0, 1, count, count, 0, 0, 0) and
            tuple(final[field] for field in snapshot_fields) == (2, 1, count, 0, 0, count, 0), "invalid_db_result")
    journal = versioned(root, "final-journal.json", "capture_entries", "terminal_entries", "release_entries")
    repeat = versioned(root, "repeat-captures.json", "observed_repeat_captures", "journal_non_atomic", "quality")
    require(journal["capture_entries"] == journal["terminal_entries"] == count and
            journal["release_entries"] == repeat["observed_repeat_captures"] == 0, "invalid_journal_counts")
    readiness = versioned(root, "readiness.json", "workers")["workers"]
    require(readiness.keys() == WORKERS and len(set(readiness.values())) == 2, "workers_not_ready")
    participation = versioned(root, "worker-journal.json", "workers", "unique_sent")
    workers = participation["workers"]
    require(participation["unique_sent"] == count and workers.keys() == WORKERS and
            sum(worker["sent"] for worker in workers.values()) == count and all(
                worker.keys() == {"sent", "unique_sent", "restart_count", "oom", "exit_code"} and
                worker["sent"] == worker["unique_sent"] > 0 and worker["restart_count"] == worker["exit_code"] == 0 and
                worker["oom"] is False for worker in workers.values()), "invalid_worker_participation")
    inventory = versioned(root, "inventory.json", "backend", "backend_basis", "vm_cpu_cores", "vm_memory_bytes",
                          "container_os", "vm_swap_bytes", "containers", "memory_semantics", "cpu_denominator",
                          "io_precision")
    containers = inventory["containers"]
    expected_images = {role: images["worker" if role in WORKERS else role] for role in WORKERS | {"api", "db", "http"}}
    require(inventory["container_os"] == "linux" and containers.keys() == expected_images.keys() and
            manifest["image_digests"] == expected_images and
            all(item["image"] == expected_images[role] for role, item in containers.items()), "image_id_mismatch")
    limits = {role: {key: item[key] for key in ("memory", "nano_cpus", "cpu_period", "cpu_quota", "cpuset")}
              for role, item in containers.items()}
    stand = {"limits": limits, "settings": manifest["effective_settings"], "load": manifest["load"],
             "vm": [inventory["backend"], inventory["vm_cpu_cores"], inventory["vm_memory_bytes"]]}
    require(comparison is None or stand == comparison, "stand_configuration_changed")
    for stage in ("initial", "final"):
        clocks = versioned(root, f"clock-{stage}.json", "api", "http", "postgres", *WORKERS)
        require(all(item.keys() == {"offset_s", "uncertainty_s", "request_utc_s", "response_utc_s"} and
                    0 <= item["uncertainty_s"] and abs(item["offset_s"]) + item["uncertainty_s"] <= 1
                    for key, item in clocks.items() if key != "schema_version"), "uncertain_clocks")
    processing = versioned(root, "processing-epoch.json", "request_start_utc", "elapsed_start_s", "response_end_utc",
                           "elapsed_end_s", "business_started_at")
    completion = versioned(root, "completion.json", "previous", "confirmed")
    result = throughput(count, processing["elapsed_start_s"], completion)
    resource = {"files_bytes": sum(path.stat().st_size for path in root.rglob("*") if path.is_file())}
    if protocol["measurement"] == "on":
        validate_on(root, manifest, readiness, workers, processing["elapsed_start_s"],
                    completion["confirmed"]["response_s"])
        cost = versioned(root, "collector-cost.json", "cpu_s", "peak_working_set_bytes", "child_process_cpu_included")
        require(cost["cpu_s"] >= 0 and cost["peak_working_set_bytes"] is not None and
                cost["peak_working_set_bytes"] > 0 and cost["child_process_cpu_included"] is False,
                "missing_collector_cost")
        resource.update(peak_working_set_bytes=cost["peak_working_set_bytes"], cpu_cores=cost["cpu_s"] / result["duration_s"])
    else:
        require(not manifest["participants"] and not any((root / name).exists() for name in
                ("processes", "samples", "collector-cost.json", "metrics-schema.json", ".byte-budget")),
                "unexpected_off_telemetry")
    result["resources"] = resource
    result["resource_budget"] = ("over_budget" if resource["files_bytes"] > 2048*MIB or
        resource.get("peak_working_set_bytes", 0) > 128*MIB or resource.get("cpu_cores", 0) > 0.1 else "pass")
    require({path.name for path in root.glob("latency-*.json")} ==
            ({f"latency-{role}.json" for role in WORKERS} if protocol["latency_probe"] else set()),
            "unexpected_latency_artifacts")
    if protocol["latency_probe"]:
        result["p95_ms"] = pooled_p95(root, workers, count)
    result["sent_per_worker"] = {role: worker["sent"] for role, worker in workers.items()}
    result["request_start_utc"] = processing["request_start_utc"]
    result["first_confirmation_utc"] = completion["confirmed"]["response_utc"]
    result["checksums_sha256"] = sha256(root / "checksums.json")
    return result, stand


def git(root, *args):
    return command(["git", "-C", str(root), *args]).strip()


def preflight(root, output):
    require(os.name == "nt" and not output.is_relative_to(root) and root != REPO, "invalid_baseline_paths")
    require(git(REPO, "branch", "--show-current") == "gv" and
            git(root, "rev-parse", "HEAD") == git(root, "rev-parse", TAG + "^{commit}") == PRODUCT_SHA and
            not git(root, "status", "--porcelain", "--untracked-files=all") and
            not git(root, "ls-files", "--others", "--ignored", "--exclude-standard"),
            "product_checkout_mismatch")
    host, _ = host_snapshot()
    inventory = docker_inventory({})
    free = shutil.disk_usage(output).free
    require(inventory["container_os"] == "linux" and free >= 4*MIB*1024, "insufficient_baseline_resources")
    return {"docker": inventory, "host": host, "free_disk_bytes": free}


def prepare_images(root, output):
    receipt = output / "images.json"
    if receipt.exists():
        result = versioned(output, "images.json", "product_sha", "image_ids")
        require(result["product_sha"] == PRODUCT_SHA, "image_provenance_mismatch")
        for image in result["image_ids"].values():
            require(command(["docker", "image", "inspect", "--format", "{{.Id}}", image]).strip() == image,
                    "missing_baseline_image")
        return result["image_ids"]
    template = compose_profile(str(uuid.uuid4()), output, product_root=root)["services"]
    build = {role: {"image": "packcall-baseline-50a3ac3:" + name, "build": template[source]["build"]}
             for role, name, source in (("api", "api", "api"), ("worker", "worker", "worker0"),
                                        ("migrator", "migrator", "migrator"),
                                        ("http", "testemailoperator", "http"))}
    with tempfile.TemporaryDirectory(prefix="packcall-baseline-images-") as temporary:
        compose = Path(temporary) / "compose.json"
        atomic(compose, {"services": build})
        compose_output(["-p", "packcall-baseline-images", "-f", str(compose), "build"], timeout=1200)
    tags = {role: item["image"] for role, item in build.items()} | {"db": template["db"]["image"]}
    try:
        command(["docker", "image", "inspect", tags["db"]])
    except CollectionError:
        command(["docker", "pull", tags["db"]], timeout=300)
    images = {role: command(["docker", "image", "inspect", "--format", "{{.Id}}", tag]).strip()
              for role, tag in tags.items()}
    atomic(receipt, {"schema_version": 1, "product_sha": PRODUCT_SHA, "image_ids": images})
    return images


def tool_revision():
    sources = [path for path in Path(__file__).resolve().parent.rglob("*") if path.is_file() and
               path.suffix in {".py", ".sql", ".json", ".txt", ".cs", ".csproj"} and
               not {"obj", "bin", "__pycache__"}.intersection(path.parts)]
    return {"commit": git(REPO, "rev-parse", "HEAD"), "working_sources_sha256":
            digest({path.relative_to(REPO).as_posix(): sha256(path) for path in sources})}


def experiment(args):
    root, output = args.product_root.resolve(), args.output.resolve()
    require(not output.is_relative_to(root) and root != REPO, "invalid_baseline_paths")
    output.mkdir(parents=True, exist_ok=True)
    resources = preflight(root, output)
    images = prepare_images(root, output)
    revision = tool_revision()
    run_root = output / str(uuid.uuid4())
    run_root.mkdir()
    probe_source = Path(__file__).resolve().parent / "latency-probe/LatencyProbe.csproj"
    count = 6000 if args.smoke else 300_000
    profile = "small-http" if args.smoke else "windows-http-300k"
    report = {"schema_version": 1, "profile": profile, "count": count, "status": "in_progress",
              "product_sha": PRODUCT_SHA, "tool_revision": revision, "image_ids": images, "preflight": resources,
              "start_utc": utc(), "sessions": [],
              "tools": {"docker_compose": command(["docker", "compose", "version", "--short"]).strip()}}
    atomic(run_root / "series.json", report)
    print(run_root, flush=True)
    stand = None
    probe = None
    def publish(status):
        report["status"] = status
        report["updated_utc"] = utc()
        atomic(run_root / "series.json", report)
    def arm(mode, latency, pair, attempt):
        nonlocal stand
        protocol = {"schema_version": 1, "measurement": mode, "latency_probe": latency, "pair": pair,
                    "attempt": attempt, "tool_revision": revision, "product_sha": PRODUCT_SHA,
                    "latency_probe_sha256": sha256(probe) if latency else None}
        params = argparse.Namespace(output=run_root, profile=profile, workers=2, count=count, prepare_only=False)
        before = set(run_root.iterdir())
        session = None
        entry = {"protocol": protocol, "status": "running"}
        report["sessions"].append(entry)
        publish("in_progress")
        try:
            session = run(params, product_root=root, enabled=mode == "on", images=images,
                          latency_probe=probe if latency else None, protocol=protocol)
            result, stand = assess(session, count, protocol, images, stand)
            entry.update(status="valid", **result)
            return result
        except Exception as error:
            entry.update(status="invalid", code=str(error) if isinstance(error, CollectionError) else "invalid_session")
            raise CollectionError(entry["code"]) from None
        except KeyboardInterrupt:
            entry["status"] = "interrupted"
            raise
        finally:
            if session is None:
                session = next((path for path in set(run_root.iterdir()) - before if path.is_dir()), None)
            if session is not None:
                entry["session_id"] = session.name
                try:
                    cleanup_owned(session, network=True)
                except Exception:
                    entry["cleanup"] = "failed"
                    if entry["status"] == "valid":
                        raise CollectionError("session_cleanup_failed") from None
            publish("in_progress")
    try:
        if args.smoke:
            for mode in ("off", "on"):
                arm(mode, False, 1, 1)
        else:
            for attempt in range(1, args.attempts + 1):
                pairs = [{mode: arm(mode, False, pair, attempt) for mode in order}
                         for pair, order in enumerate(THROUGHPUT_ORDER, 1)]
                report.setdefault("throughput", []).append(throughput_gate(pairs))
                if report["throughput"][-1]["status"] != "inconclusive":
                    break
            if report["throughput"][-1]["status"] != "pass":
                publish(report["throughput"][-1]["status"])
                return report["status"]
            if any(entry["resource_budget"] != "pass" for entry in report["sessions"]):
                publish("over_budget")
                return report["status"]
        with tempfile.TemporaryDirectory(prefix="packcall-baseline-probe-") as temporary:
            command(["dotnet", "build", str(probe_source), "-c", "Release", "-o", temporary, "--nologo"], timeout=120)
            probe = Path(temporary) / "LatencyProbe.dll"
            for pair, order in enumerate(LATENCY_ORDER[:1] if args.smoke else LATENCY_ORDER, 1):
                results = {mode: arm(mode, True, pair, 1) for mode in order}
                off, on = results["off"]["p95_ms"], results["on"]["p95_ms"]
                require(off > 0, "invalid_latency_denominator")
                report.setdefault("latency", []).append({"pair": pair, "off_ms": off, "on_ms": on,
                                                          "increase_pct": 100 * (on / off - 1)})
        resource_ok = all(entry["resource_budget"] == "pass" for entry in report["sessions"])
        latency_ok = all(latency_in_budget(pair["off_ms"], pair["on_ms"]) for pair in report["latency"])
        publish("smoke_pass" if args.smoke else "pass_pending_privacy_review" if resource_ok and latency_ok else "over_budget")
    except CollectionError as error:
        report["failure_code"] = str(error)
        publish("invalid")
        raise
    except KeyboardInterrupt:
        publish("interrupted")
    return report["status"]


def latency_in_budget(off, on):
    limit = off * 1.1
    return on <= limit or math.isclose(on, limit, rel_tol=1e-12)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--product-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--attempts", type=int, default=2, help="Repeat an inconclusive throughput series up to this limit")
    arguments = parser.parse_args()
    if arguments.attempts < 2:
        parser.error("invalid attempt limit")
    try:
        status = experiment(arguments)
    except CollectionError as error:
        parser.exit(1, f"Baseline failed: {error}\n")
    parser.exit(0 if status in {"smoke_pass", "pass_pending_privacy_review"} else 2, f"{status}\n")
