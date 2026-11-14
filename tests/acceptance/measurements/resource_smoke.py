"""Small live collector verification using a new disposable PostgreSQL container."""
import sys
from pathlib import Path as _Path
_PERFORMANCE = _Path(__file__).resolve().parents[3] / "scripts/performance"
sys.path.insert(0, str(_PERFORMANCE))
import json
from pathlib import Path
import subprocess
import time
import uuid
from collectors import command, docker_inventory, container_snapshot, host_snapshot, postgres_snapshot, queue_snapshot, sql_snapshot, CollectionError, ScheduledCollector
from contract import validate


def run():
    name = "packcall-perf-resources-" + uuid.uuid4().hex
    session, campaign, paused = (str(uuid.uuid4()) for _ in range(3))
    output = _PERFORMANCE.parents[1] / "artifacts/performance" / session
    output.mkdir(parents=True)
    def docker(*args, input=None): return command(["docker", *args], input=input, timeout=60)
    docker("run", "-d", "--name", name, "--label", "packcall.performance.fixture=true", "-e", "POSTGRES_HOST_AUTH_METHOD=trust", "postgres:18.6-alpine3.24")
    try:
        for _ in range(60):
            try:
                docker("exec", name, "pg_isready", "-U", "postgres")
                break
            except CollectionError: time.sleep(.5)
        else: raise RuntimeError("fixture_not_ready")
        sql = f"""
CREATE TABLE campaigns(id uuid, status integer);
CREATE TABLE deliveries(campaign_id uuid, delivery_status integer, lock_expires_at timestamptz,
                        created_at timestamptz, channel text, address_snapshot text);
CREATE TABLE delivery_status_journal(id integer);
INSERT INTO campaigns (id, status) VALUES ('{campaign}',1), ('{paused}',4);
INSERT INTO deliveries (campaign_id, delivery_status, lock_expires_at, created_at, channel, address_snapshot)
VALUES ('{campaign}',0,NULL,now()-interval '1 hour','email','valid@example.test'),
('{campaign}',1,now()-interval '1 second',now()-interval '3 hours','email','recovered@example.test'),
('{campaign}',2,NULL,now(),'email','sent@example.test'),
('{campaign}',-1,NULL,now(),'email','failed@example.test'),
('{paused}',0,NULL,now(),'email','paused@example.test'),
('{campaign}',0,NULL,now()-interval '2 hours','telegram','123'),
('{campaign}',0,NULL,now()-interval '2 hours','email',NULL),
('{campaign}',0,NULL,now()-interval '2 hours','email',E' \t');
CREATE ROLE perf_read LOGIN;
GRANT CONNECT ON DATABASE postgres TO perf_read;
GRANT USAGE ON SCHEMA public TO perf_read;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO perf_read;
GRANT pg_read_all_stats TO perf_read;
CREATE ROLE perf_denied LOGIN;
"""
        docker("exec", "-i", name, "psql", "-U", "postgres", "-qAt", "-v", "ON_ERROR_STOP=1", input=sql)
        metrics, _ = queue_snapshot(name, "postgres", campaign, "perf_read")
        assert metrics["queue.waiting_eligible"]["value"] == 1
        assert metrics["queue.waiting_total"]["value"] == 4
        assert metrics["queue.expired"]["value"] == 1
        assert 3600 <= metrics["queue.oldest_eligible_age_s"]["value"] < metrics["queue.oldest_waiting_age_s"]["value"]
        docker("exec", "-i", name, "psql", "-U", "postgres", "-qAt", "-v", "ON_ERROR_STOP=1",
               input=f"UPDATE deliveries SET delivery_status=0, lock_expires_at=NULL WHERE campaign_id='{campaign}' AND delivery_status=1;")
        recovered, _ = queue_snapshot(name, "postgres", campaign, "perf_read")
        assert recovered["queue.waiting_eligible"]["value"] == 2
        assert 10800 <= recovered["queue.oldest_eligible_age_s"]["value"] < 10920
        paused_metrics, _ = queue_snapshot(name, "postgres", paused, "perf_read")
        assert paused_metrics["queue.waiting_total"]["value"] == 1
        assert paused_metrics["queue.waiting_eligible"]["value"] == 0
        empty, _ = queue_snapshot(name, "postgres", str(uuid.uuid4()), "perf_read")
        assert empty["queue.oldest_waiting_age_s"]["value"] is None
        try:
            queue_snapshot(name, "postgres", campaign, "perf_denied")
            raise AssertionError("permissions should fail")
        except CollectionError as error: assert str(error) == "permission_denied"
        start = time.monotonic()
        try:
            docker("exec", "-i", name, "psql", "-U", "postgres", "-qAt", "-v", "ON_ERROR_STOP=1",
                   input="\\set VERBOSITY sqlstate\nBEGIN READ ONLY; SET LOCAL statement_timeout='50ms'; SELECT pg_sleep(2); COMMIT;")
            raise AssertionError("timeout expected")
        except CollectionError as error: assert str(error) == "timeout"
        assert time.monotonic() - start < 2
        inventory = docker_inventory({"db": name})
        (output / "inventory.json").write_text(json.dumps(inventory, indent=2))
        for source, collect in [("postgres", lambda: postgres_snapshot(name,"postgres","perf_read",True)),
                                ("queue", lambda: queue_snapshot(name,"postgres",campaign,"perf_read")),
                                ("containers", lambda: container_snapshot({"db":name})), ("host", host_snapshot)]:
            record = ScheduledCollector(session,source,15,collect).tick()
            validate("sample",record)
            if source == "postgres":
                assert record["metrics"]["postgres.working_connections"]["value"] == 0
                assert "postgres.wal_bytes" in record["metrics"]
                assert "postgres.table.deliveries.table_bytes" in record["metrics"]
            (output / f"{source}.json").write_text(json.dumps(record,indent=2))
        print("PASS: read-only PG stats/queue, paused/empty/age, denied role, bounded SQL timeout, Docker and Windows samples; " + str(output))
    finally: docker("rm", "-f", name)


if __name__ == "__main__": run()
