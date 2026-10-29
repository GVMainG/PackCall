"""Allowlisted read-only collectors. Never export raw command diagnostics or query text."""
import ctypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parent


class CollectionError(RuntimeError):
    pass


def command(args, *, input=None, timeout=10):
    try:
        result = subprocess.run(args, input=input, text=True, capture_output=True, timeout=timeout, encoding="utf-8", errors="replace")
    except subprocess.TimeoutExpired:
        raise CollectionError("timeout") from None
    except OSError:
        raise CollectionError("unsupported") from None
    if result.returncode:
        code = "permission_denied" if "42501" in result.stderr else "timeout" if "57014" in result.stderr else "collection_error"
        raise CollectionError(code)
    return result.stdout


def scalar(value, unit="events", kind="gauge", reason=None):
    return dict(value=value, unit=unit, kind=kind, missing_reason=reason or ("no_data" if value is None else None))


def utc():
    return datetime.now(timezone.utc).isoformat()


def sql_snapshot(container, database, script, campaign=None, user="postgres", timeout_ms=500):
    if script not in {"queue", "postgres", "postgres_extra", "tables"}:
        raise CollectionError("unsupported")
    sql = "\\set VERBOSITY sqlstate\nBEGIN READ ONLY;\nSET LOCAL statement_timeout = '%dms';\n" % timeout_ms
    sql += (ROOT / "sql" / f"{script}.sql").read_text() + "\nCOMMIT;\n"
    args = ["docker", "exec", "-i", "-e", "PGAPPNAME=packcall-performance-collector", container,
            "psql", "-U", user, "-d", database, "-qAt", "-v", "ON_ERROR_STOP=1"]
    if campaign:
        args += ["-v", "campaign_id=" + str(uuid.UUID(campaign))]
    try:
        return json.loads(command(args, input=sql, timeout=timeout_ms / 1000 + 5))
    except (ValueError, TypeError):
        raise CollectionError("collection_error") from None


def queue_snapshot(container, database, campaign, user="postgres"):
    values = sql_snapshot(container, database, "queue", campaign, user, 1000)
    metrics = {"queue." + key: scalar(value, "s" if key.endswith("_s") else "deliveries")
               for key, value in values.items() if key != "db_now"}
    return metrics, {"db_now": values["db_now"], "campaign_id": campaign}


def postgres_snapshot(container, database, user="postgres", tables=False):
    values = sql_snapshot(container, database, "postgres", user=user)
    context = {key: values[key] for key in ("db_now", "server_start", "stats_reset", "track_io_timing", "track_wal_io_timing")}
    metrics = {}
    for key, value in values.items():
        if key not in context and key != "activity":
            metrics["postgres." + key] = scalar(value, "bytes" if key.endswith("bytes") else "events", "gauge" if key.endswith("connections") else "cumulative")
    # Values originate from PostgreSQL's fixed activity categories; bound cardinality and syntax.
    for row in values["activity"][:128]:
        label = ".".join(re.sub("[^a-zA-Z0-9_]", "_", row[key] or "unknown") for key in ("state", "wait_event_type", "wait_event"))
        metrics["postgres.activity." + label] = scalar(row["count"], "connections")
    try:
        extra = sql_snapshot(container, database, "postgres_extra", user=user)
        for key, value in extra.items():
            if key.endswith("reset"):
                context[key] = value
            else:
                metrics["postgres." + key] = scalar(value, "bytes" if key.endswith("bytes") else "events", "cumulative")
    except CollectionError as error:
        metrics["postgres.extended_stats"] = scalar(None, reason=str(error))
    if tables:
        try:
            for row in sql_snapshot(container, database, "tables", user=user):
                table = row.pop("relname")
                if table not in {"deliveries", "delivery_status_journal", "campaigns"}: continue
                for key, value in row.items():
                    metrics[f"postgres.table.{table}.{key}"] = scalar(value, "bytes" if key.endswith("bytes") else "s" if key.endswith("_s") else "events")
        except CollectionError as error:
            metrics["postgres.tables"] = scalar(None, reason=str(error))
    return metrics, context


def parse_bytes(text):
    match = re.fullmatch(r"\s*([0-9.]+)\s*(B|kB|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\s*", text)
    if not match: raise CollectionError("collection_error")
    units = {"B": 1, "kB": 1000, "KB": 1000, "MB": 1000**2, "GB": 1000**3, "TB": 1000**4,
             "KiB": 1024, "MiB": 1024**2, "GiB": 1024**3, "TiB": 1024**4}
    return float(match[1]) * units[match[2]]


def docker_inventory(containers):
    info = json.loads(command(["docker", "info", "--format", '{"cpu":{{.NCPU}},"memory":{{.MemTotal}},"os":{{json .OSType}},"kernel":{{json .KernelVersion}}}']))
    backend = "wsl2" if "wsl2" in info["kernel"].lower() else "hyperv" if "linuxkit" in info["kernel"].lower() else "unknown"
    inventory = {"backend": backend, "backend_basis": "kernel", "vm_cpu_cores": info["cpu"], "vm_memory_bytes": info["memory"],
                 "container_os": info["os"], "vm_swap_bytes": None, "containers": {},
                 "memory_semantics": "docker_cli_linux_usage_minus_cache", "cpu_denominator": "one_core", "io_precision": "docker_cli_rounded_bytes"}
    template = '{"memory":{{.HostConfig.Memory}},"nano_cpus":{{.HostConfig.NanoCpus}},"cpu_period":{{.HostConfig.CpuPeriod}},"cpu_quota":{{.HostConfig.CpuQuota}},"cpuset":{{json .HostConfig.CpusetCpus}},"image":{{json .Image}},"restart_count":{{.RestartCount}},"oom":{{.State.OOMKilled}}}'
    for role, container in containers.items():
        if role not in {"worker", "api", "db", "http", "collector"} and not re.fullmatch("worker[0-9]+", role): raise CollectionError("unsupported")
        values = json.loads(command(["docker", "inspect", "--format", template, container]))
        for key in ("memory", "nano_cpus", "cpu_quota", "cpu_period"):
            if values[key] in (0, -1): values[key] = "unlimited"
        mounts = command(["docker", "inspect", "--format", '{{range .Mounts}}{{.Type}} {{.Destination}} {{.RW}}{{println}}{{end}}', container])
        values["mounts"] = [dict(type=parts[0], destination=parts[1], writable=parts[2]=="true") for line in mounts.splitlines() if len(parts:=line.split())==3]
        inventory["containers"][role] = values
    return inventory


def container_snapshot(containers):
    metrics = {}
    try:
        rows = [json.loads(line) for line in command(["docker", "stats", "--no-stream", "--format", "{{json .}}", *containers.values()], timeout=4).splitlines()]
    except (CollectionError, ValueError) as error:
        reason = str(error) if isinstance(error, CollectionError) else "collection_error"
        return {f"container.{role}.available": scalar(None, reason=reason) for role in containers}, {}
    for role, container in containers.items():
        try:
            row = next((row for row in rows if row["Name"] == container or container.startswith(row["ID"])), None)
            if row is None: raise CollectionError("source_lost")
            metrics[f"container.{role}.cpu"] = scalar(float(row["CPUPerc"].rstrip("%")) / 100, "cores")
            for column, keys, kind in (("MemUsage", ("memory", "memory_limit"), "gauge"), ("NetIO", ("network_rx", "network_tx"), "cumulative"), ("BlockIO", ("block_read", "block_write"), "cumulative")):
                for key, value in zip(keys, row[column].split("/")):
                    metrics[f"container.{role}.{key}"] = scalar(parse_bytes(value), "bytes", kind)
            metrics[f"container.{role}.throttled"] = scalar(None, "s", "cumulative", "unsupported")
            state = json.loads(command(["docker", "inspect", "--format", '{"restarts":{{.RestartCount}},"oom":{{.State.OOMKilled}},"running":{{.State.Running}},"health":{{if .State.Health}}{{json .State.Health.Status}}{{else}}null{{end}}}', container], timeout=2))
            for key in ("restarts", "oom", "running"):
                metrics[f"container.{role}.{key}"] = scalar(int(state[key]))
            metrics[f"container.{role}.healthy"] = scalar(None if state["health"] is None else int(state["health"] == "healthy"), reason="unsupported" if state["health"] is None else None)
        except (CollectionError, ValueError, KeyError) as error:
            metrics[f"container.{role}.available"] = scalar(None, reason=str(error) if isinstance(error, CollectionError) else "collection_error")
    return metrics, {}


def host_snapshot():
    script = "$o=Get-CimInstance Win32_OperatingSystem; $c=Get-CimInstance Win32_Processor; [pscustomobject]@{total_bytes=[double]$o.TotalVisibleMemorySize*1024;available_bytes=[double]$o.FreePhysicalMemory*1024;cpu_cores=($c|Measure-Object NumberOfLogicalProcessors -Sum).Sum;cpu_percent=($c|Measure-Object LoadPercentage -Average).Average}|ConvertTo-Json -Compress"
    values = json.loads(command(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script], timeout=5))
    return {"host.ram_total": scalar(values["total_bytes"], "bytes"), "host.ram_available": scalar(values["available_bytes"], "bytes"),
            "host.cpu": scalar(None if values["cpu_percent"] is None else values["cpu_percent"] / 100 * values["cpu_cores"], "cores"),
            "host.disk_io": scalar(None, "bytes", "cumulative", "unsupported")}, {}


def collector_rss():
    if os.name != "nt": return None
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("faults", ctypes.c_ulong)] + [(name, ctypes.c_size_t) for name in ("peak", "working", "page_pool_peak", "page_pool", "nonpage_peak", "nonpage", "pagefile", "pagefile_peak")]
    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    ctypes.windll.kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    ctypes.windll.psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_ulong]
    handle = ctypes.windll.kernel32.GetCurrentProcess()
    if not ctypes.windll.psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb): return None
    return counters.working


class ScheduledCollector:
    """One caller performs one bounded sample at a time; skipped deadlines never catch up."""
    def __init__(self, session, source, period, collect, epoch=None, clock=time.monotonic):
        self.session, self.source, self.period, self.collect = session, source, period, collect
        self.clock = clock
        self.epoch = clock() if epoch is None else epoch
        self.due = clock()
        self.instance = str(uuid.uuid4())
        self.sequence = 0
        self.previous_context = None

    def tick(self):
        start = self.clock()
        if start < self.due: return None
        start_utc, cpu = utc(), time.process_time()
        lag = start - self.due
        quality = "complete"
        try:
            metrics, context = self.collect()
        except CollectionError as error:
            metrics, context, quality = {"collector.available": scalar(None, reason=str(error))}, {}, "missing"
        end = self.clock()
        if self.previous_context is not None and any(
            context.get(key) != self.previous_context.get(key)
            for key in ("server_start", "stats_reset", "wal_stats_reset", "io_stats_reset", "checkpointer_stats_reset")
            if key in context or key in self.previous_context
        ):
            quality = "reset"
        if context:
            self.previous_context = dict(context)
        metrics["collector.duration"] = scalar(end - start, "s")
        metrics["collector.cpu"] = scalar(time.process_time() - cpu, "s", "delta")
        rss = collector_rss()
        metrics["collector.rss"] = scalar(rss, "bytes", reason=("unsupported" if os.name != "nt" else "collection_error") if rss is None else None)
        if lag > self.period and quality == "complete": quality = "gap"
        record = dict(schema_version=1, session_id=self.session, source=self.source, instance_id=self.instance,
                      sequence=self.sequence, start_utc=start_utc, end_utc=utc(), duration_s=end-start,
                      elapsed_start_s=start-self.epoch, elapsed_end_s=end-self.epoch, quality=quality,
                      metrics=metrics, period_s=self.period, lag_s=lag, receipt_utc=utc(), context=context)
        self.sequence += 1
        self.due = end + self.period
        return record
