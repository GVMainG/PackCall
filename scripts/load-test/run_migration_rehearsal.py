"""Measure the rehearsal against an isolated, pre-seeded Docker PostgreSQL."""

import hashlib
import json
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from pathlib import Path


NETWORK = "packcall-rehearsal-23"
DB = f"{NETWORK}-db"
OPERATOR = f"{NETWORK}-operator"
OUTPUT = Path("artifacts/story-2-3")
INITIAL = "20261028193126_InitialCreate"
MIGRATION = "20261113214910_ContactsChannels"
SMOKE_ID = "c7e37c4b-1ecb-4f3c-abe9-36b6a5db07c1"


def docker(*args, timeout=1800, check=True):
    return subprocess.run(
        ["docker", *args], capture_output=True, text=True, encoding="utf-8",
        timeout=timeout, check=check,
    )


def pg(database, *args):
    return docker("exec", DB, "psql", "-XAtq", "-v", "ON_ERROR_STOP=1",
                  "-U", "postgres", "-d", database, *args).stdout.strip()


@contextmanager
def measure(results, stage):
    start = time.monotonic()
    try:
        yield
    finally:
        results[stage] = round(time.monotonic() - start, 3)
        print(f"{stage}: {results[stage]} s", flush=True)


def runtime_env(database):
    return (
        "-e", f"ConnectionStrings__Postgres=Host={DB};Database={database};Username=postgres;Application Name=story23-{database}",
        "-e", "PACKCALL_ENVIRONMENT=test",
        "-e", "TEST_SENDER_MODE=http", "-e", "TEST_SIMULATE_ERRORS=false",
        "-e", f"TEST_OPERATOR_URL=http://{OPERATOR}:8000", "-e", "TEST_OPERATOR_TIMEOUT_SECONDS=5",
    )


def await_ready(probe, description):
    deadline = time.monotonic() + 75
    while time.monotonic() < deadline:
        if probe():
            return
        time.sleep(0.25)
    raise RuntimeError(f"Timed out waiting for {description}")


def api_request(database, path, method="GET"):
    return docker("exec", OPERATOR, "python", "-c",
                  "import urllib.request as u, sys; "
                  "print(u.urlopen(u.Request(sys.argv[1], method=sys.argv[2]), timeout=3).read().decode())",
                  f"http://{NETWORK}-{database}-api:8080/api/{path}", method,
                  timeout=5, check=False)


def start_services(database, migration):
    image = f"packcall-rehearsal-{database}"
    for service in ("api", "worker"):
        docker("run", "-d", "--name", f"{NETWORK}-{database}-{service}", "--network", NETWORK,
               *runtime_env(database), f"{image}:{service}")

    def ready():
        api_log = docker("logs", f"{NETWORK}-{database}-api").stdout
        worker_log = docker("logs", f"{NETWORK}-{database}-worker").stdout
        return (f"Schema ready. Version={migration}" in api_log
                and f"Schema ready. Version={migration}" in worker_log
                and "Фоновый обработчик доставок запущен" in worker_log
                and api_request(database, "campaigns?pageSize=1").returncode == 0)

    await_ready(ready, f"{database} API and Worker")


def probe_read(table):
    started = time.monotonic()
    result = docker("exec", "-e", "PGAPPNAME=story23-probe", DB, "psql", "-XAt",
                    "-U", "postgres", "-d", "candidate", "-c", "SET statement_timeout='60s'",
                    "-c", f"SELECT 1 FROM {table} LIMIT 1", timeout=65, check=False)
    return {
        "table": table, "seconds": round(time.monotonic() - started, 3),
        "result": "ok" if result.returncode == 0 else result.stderr.strip(),
    }


def migrate(evidence):
    lock_sql = """
        SELECT clock_timestamp(), a.pid, a.application_name, l.relation::regclass,
               l.mode, l.granted, pg_blocking_pids(a.pid)
        FROM pg_locks l JOIN pg_stat_activity a USING (pid)
        WHERE a.datname = current_database() AND a.pid <> pg_backend_pid()
          AND l.locktype = 'relation' ORDER BY a.pid, l.relation, l.mode
    """
    tables = ("campaigns", "deliveries", "recipients")
    with ThreadPoolExecutor(max_workers=4) as pool:
        migration = pool.submit(docker, "run", "--rm", "--network", NETWORK,
                                *runtime_env("candidate"), "packcall-rehearsal-candidate:migrator")
        waiting_reads = None
        with (OUTPUT / "locks.tsv").open("w", encoding="utf-8") as samples:
            while not migration.done():
                current = pg("candidate", "-c", lock_sql)
                if current:
                    samples.write(current + "\n")
                    samples.flush()
                if waiting_reads is None and "|AccessExclusiveLock|t|" in current:
                    waiting_reads = [pool.submit(probe_read, table) for table in tables]
                time.sleep(0.1)
        result = migration.result()
        (OUTPUT / "migrator.log").write_text(result.stdout + result.stderr, encoding="utf-8")
        if waiting_reads is None:
            raise RuntimeError("Did not observe the exclusive migration lock")
        evidence["migration_reads"] = [read.result() for read in waiting_reads]


def restore(database):
    docker("exec", DB, "createdb", "-U", "postgres", database)
    docker("exec", DB, "pg_restore", "--exit-on-error", "-U", "postgres",
           "-d", database, "/tmp/pre-migration.dump")


def verify(database, expected, migration, migrated=False, normalized=False):
    history = pg(database, "-c", 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1')
    if history.splitlines() != migration:
        raise RuntimeError(f"Unexpected EF migration history in {database}")
    observed = pg(database, "-v", f"migrated={str(migrated).lower()}", "-v",
                  f"normalized={str(normalized).lower()}", "-f", "/tmp/check.sql")
    if observed != expected:
        raise RuntimeError(f"Rehearsal fingerprints differ in {database}")
    return observed


def smoke(database):
    # The API's create operation selects all recipients, so prepare one delivery directly.
    person_id = "md5('smoke-person')::uuid"
    address = "'smoke@example.invalid'"
    delivery_id = "md5('smoke-delivery')::uuid"
    campaign = f"'{SMOKE_ID}'"
    if database == "candidate":
        audience = f"""
            INSERT INTO contacts(id) VALUES ({person_id});
            INSERT INTO contact_channels(id, contact_id, channel, address)
            VALUES ({person_id}, {person_id}, 'email', {address});
            INSERT INTO campaign_contacts VALUES ({campaign}, {person_id});
            INSERT INTO campaign_channels VALUES ({campaign}, 'email');
        """
        columns = "contact_id, address_snapshot, channel"
        values = f"{person_id}, {address}, 'email'"
    else:
        audience = f"INSERT INTO recipients(id, email) VALUES ({person_id}, {address});"
        columns = "recipient_id, recipient_email"
        values = f"{person_id}, {address}"
    pg(database, "-c", f"""
        BEGIN;
        INSERT INTO campaigns(id, name, message_text, status, created_at, updated_at)
        VALUES ({campaign}, 'Rehearsal smoke', 'Synthetic smoke message', 0, now(), now());
        {audience}
        INSERT INTO deliveries(id, campaign_id, {columns}, delivery_status, created_at, updated_at)
        VALUES ({delivery_id}, {campaign}, {values}, 0, now(), now());
        COMMIT;
    """)
    if api_request(database, f"campaigns/{SMOKE_ID}/start", "POST").returncode:
        raise RuntimeError(f"API start failed in {database}")

    def sent():
        response = api_request(database, f"campaigns/{SMOKE_ID}")
        if response.returncode or json.loads(response.stdout)["campaign"]["status"] != "Completed":
            return False
        return pg(database, "-c", f"""
            SELECT d.delivery_status, j.operator_result
            FROM deliveries d JOIN delivery_status_journal j ON j.delivery_id=d.id
            WHERE d.id={delivery_id} AND j.new_status=2
        """) == "2|AcceptedByProvider"

    await_ready(sent, f"{database} test operator acceptance")


def run(evidence):
    timings = evidence["seconds"]
    docker("run", "-d", "--read-only", "--name", OPERATOR, "--network", NETWORK,
           "packcall-rehearsal:testoperator")
    await_ready(lambda: docker("exec", OPERATOR, "python", "-c",
                              "import urllib.request; urllib.request.urlopen('http://localhost:8000/healthz', timeout=2)",
                              timeout=5, check=False).returncode == 0, "test operator")
    fixture = "scripts/load-test/check_migration_rehearsal.sql"
    docker("cp", fixture, f"{DB}:/tmp/check.sql")
    if pg("source", "-c", 'SELECT "MigrationId" FROM "__EFMigrationsHistory"') != INITIAL:
        raise RuntimeError("Expected the beta.1 schema")
    if pg("source", "-c", "SELECT count(*) FROM deliveries") != "1000000":
        raise RuntimeError("Expected the seeded source")
    evidence["source_bytes"] = int(pg("source", "-c", "SELECT pg_database_size(current_database())"))
    with measure(timings, "source_fingerprints"):
        raw = pg("source", "-v", "migrated=false", "-v", "normalized=false", "-f", "/tmp/check.sql")
        normalized = pg("source", "-v", "migrated=false", "-v", "normalized=true", "-f", "/tmp/check.sql")
    evidence["source"] = raw.splitlines()
    evidence["normalized_source"] = normalized.splitlines()
    with measure(timings, "apply_window"):
        with measure(timings, "dump"):
            docker("exec", DB, "pg_dump", "-Fc", "-U", "postgres", "-f", "/tmp/pre-migration.dump", "source")
        with measure(timings, "restore_candidate"):
            restore("candidate")
        with measure(timings, "control_reads"):
            evidence["control_reads"] = [probe_read(table) for table in ("campaigns", "deliveries", "recipients")]
        with measure(timings, "migrator_with_sampling"):
            migrate(evidence)
        with measure(timings, "verify_candidate"):
            verify("candidate", normalized, [INITIAL, MIGRATION], migrated=True)
        with measure(timings, "candidate_readiness"):
            start_services("candidate", MIGRATION)

    with measure(timings, "rollback_window"):
        with measure(timings, "restore_rollback"):
            restore("rollback")
        with measure(timings, "verify_rollback"):
            verify("rollback", raw, [INITIAL])
        with measure(timings, "rollback_readiness"):
            start_services("rollback", INITIAL)

    with measure(timings, "candidate_smoke"):
        smoke("candidate")
    with measure(timings, "rollback_smoke"):
        smoke("rollback")
    archive = OUTPUT / "pre-migration.dump"
    docker("cp", f"{DB}:/tmp/pre-migration.dump", str(archive))
    with archive.open("rb") as stream:
        evidence["archive_sha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
    evidence["archive_bytes"] = archive.stat().st_size
    evidence["window_total_seconds"] = timings["apply_window"] + timings["rollback_window"]
    if evidence["window_total_seconds"] > 1800:
        raise RuntimeError("Combined windows exceeded 30 minutes")


if __name__ == "__main__":
    OUTPUT.mkdir(parents=True, exist_ok=True)
    evidence = {"seconds": {}}
    try:
        if docker("network", "inspect", "-f", "{{.Internal}}", NETWORK).stdout.strip() != "true":
            raise RuntimeError("An internal rehearsal network is required")
        run(evidence)
        evidence["result"] = "PASS"
    except Exception as error:
        evidence["result"] = "FAIL-RECORDED"
        evidence["reason"] = str(error)
        if isinstance(error, subprocess.CalledProcessError):
            (OUTPUT / "error.log").write_text(error.stdout + error.stderr, encoding="utf-8")
        raise
    finally:
        (OUTPUT / "results.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
