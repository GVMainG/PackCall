"""Run SM-6 scenario A and provide the shared isolated Compose stand."""
import json
import os
import platform
import secrets
import subprocess
import tarfile
import tempfile
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from uuid import UUID, uuid4


ROOT = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent.relative_to(ROOT)
SCENARIOS = (
    ("timeout", "http", "TestOperatorTimeoutException"),
    ("exception", "inprocess", "InvalidOperationException"),
    ("cancellation", "inprocess", "TaskCanceledException"),
    ("mismatch", "http", "TestOperatorProtocolException"),
    ("rejection", "inprocess", "SimulatedOperatorRejected"),
)
WAITING, IN_PROGRESS, SENT, FAILED = 0, 1, 2, -1
CAMPAIGN_RUNNING = 1
COMPLETED = 2


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def wait_for(probe, description, timeout=50):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if value := probe():
            return value
        time.sleep(0.15)
    raise TimeoutError(description)


def marker(folder, attempt, phase):
    path = folder / f"attempt-{attempt}-{phase}.json"
    return wait_for(lambda: read_json(path) if path.exists() else None, path.name)


class Stand:
    def __init__(self, context, output, project):
        self.output = output
        self.project = project
        self.password = secrets.token_hex(24)
        self.env = dict(os.environ, SM6_DB_PASSWORD=self.password, SM6_CONTROL=str(output / "control"))
        self.compose_command = ["docker", "compose", "-p", project, "-f", str(context / HERE / "compose.yml")]

    def command(self, args, timeout=60):
        result = subprocess.run(args, cwd=ROOT, env=self.env, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=timeout)
        if result.returncode:
            details = self.redact(result.stderr + "\n" + result.stdout)
            raise RuntimeError(f"{args[0]} exited {result.returncode}: {details[-4000:]}")
        return result.stdout.strip()

    def redact(self, value):
        return value.replace(self.password, "[REDACTED]")

    def compose(self, *args, timeout=60):
        return self.command([*self.compose_command, *args], timeout=timeout)

    def one_off_workers(self):
        return self.command([
            "docker", "ps", "-aq", "--filter", f"label=com.docker.compose.project={self.project}",
            "--filter", "label=com.docker.compose.oneoff=True",
        ]).splitlines()

    def stop(self):
        try:
            one_off_workers = self.one_off_workers()
            if one_off_workers:
                self.command(["docker", "rm", "-f", *one_off_workers])
        finally:
            self.compose("--profile", "tools", "down", "--volumes", "--remove-orphans", "--rmi", "local", timeout=90)

    def collect_logs(self):
        try:
            logs = [self.compose("logs", "--no-color")]
            logs.extend(self.command(["docker", "logs", worker]) for worker in self.one_off_workers())
            (self.output / "failure.log").write_text(self.redact("\n".join(logs)), encoding="utf-8")
        except Exception as error:
            return self.redact(str(error))
        return None

    def sql(self, query):
        return self.compose("exec", "-T", "db", "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1",
                            "-U", "sm6", "-d", "sm6", "-c", query)

    def start(self):
        self.compose("build", "migrator", "api", "probe", timeout=1200)
        self.compose("up", "-d", "--wait", "db", "http", timeout=120)
        self.compose("up", "-d", "api", timeout=120)
        port = self.compose("port", "api", "8080").rsplit(":", 1)[1]
        self.api_root = f"http://127.0.0.1:{port}/api/"
        wait_for(self.api_ready, "API readiness", timeout=40)
        self.api("recipients", {"email": "sm6@example.test"})

    def api(self, path, body=None):
        request = urllib.request.Request(self.api_root + path,
                                        data=None if body is None else json.dumps(body).encode(),
                                        headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=10) as response:
            return json.load(response)

    def api_ready(self):
        try:
            self.api("campaigns")
            return True
        except OSError:
            return False

    def start_probe(self, scenario, *args):
        worker = f"{self.project}-{scenario}"
        self.compose("run", *args, "--no-deps", "--name", worker, "-e", f"IDENTITY={worker}",
                     "-e", f"SM6_CASE={scenario}", "probe")
        return worker

    def snapshot(self, campaign_id):
        return json.loads(self.sql(f"""
            SELECT json_build_object(
                'db_now', clock_timestamp(),
                'campaign_status', (SELECT status FROM campaigns WHERE id = '{campaign_id}'),
                'deliveries', coalesce((
                    SELECT json_agg(d ORDER BY d.id) FROM deliveries d WHERE d.campaign_id = '{campaign_id}'
                ), '[]'::json),
                'journal', coalesce((
                    SELECT json_agg(j ORDER BY j.id) FROM delivery_status_journal j WHERE j.campaign_id = '{campaign_id}'
                ), '[]'::json)
            )
        """))


def delivery(snapshot):
    require(len(snapshot["deliveries"]) == 1, "Expected exactly one delivery in the campaign")
    return snapshot["deliveries"][0]


def journal(snapshot):
    return [(row["previous_status"], row["new_status"], row["operator_result"]) for row in snapshot["journal"]]


def check_attempt(mark, delivery_id, attempt, outcome=None, lease_cancelled=False):
    require(mark["deliveryId"] == delivery_id and mark["attempt"] == attempt,
            "Operator event belongs to a different delivery or attempt")
    require(mark["outcome"] == outcome, f"Unexpected attempt outcome: {mark['outcome']}")
    require(mark["leaseCancelled"] is lease_cancelled, "Unexpected cancellation token state")


def check_wire(folder, scenario, delivery_id):
    def response(attempt):
        path = folder / f"http-{attempt}.json"
        return wait_for(lambda: read_json(path) if path.exists() else None, path.name)

    expected_first = None if scenario == "timeout" else {"result": "accepted", "code": "SimulatedOperatorRejected"}
    for attempt, body in enumerate((expected_first, {"result": "accepted", "code": "AcceptedByProvider"}), start=1):
        expected = {"delivery_id": delivery_id, "status": None, "content_type": None, "response": body}
        if body is not None:
            expected.update(status=200, content_type="application/json")
        require(response(attempt) == expected, f"Unexpected HTTP response for attempt {attempt}")


def check_active_lease(snapshot, worker):
    row = delivery(snapshot)
    require(snapshot["campaign_status"] == CAMPAIGN_RUNNING and row["delivery_status"] == IN_PROGRESS
            and row["worker_id"] == worker and row["completed_at"] is None and row["lock_expires_at"]
            and datetime.fromisoformat(row["lock_expires_at"]) > datetime.fromisoformat(snapshot["db_now"]),
            "Expected an active campaign and lease")


def run_case(stand, scenario, mode, outcome):
    folder = stand.output / "control" / scenario
    folder.mkdir()
    campaign_id = UUID(stand.api("campaigns", {"name": f"SM-6 A {scenario}", "messageText": scenario})["id"])
    stand.api(f"campaigns/{campaign_id}/start", {})
    worker = stand.start_probe(scenario, "-d", "-e", f"TEST_SENDER_MODE={mode}")

    first = marker(folder, 1, "entered")
    captured = stand.snapshot(campaign_id)
    save(folder / "captured.json", captured)
    original = delivery(captured)
    delivery_id = original["id"]
    expected_journal = [(WAITING, IN_PROGRESS, None)]
    check_active_lease(captured, worker)
    require(journal(captured) == expected_journal, "Unexpected journal at first capture")
    check_attempt(first, delivery_id, 1)
    (folder / "allow-attempt-1").touch()
    check_attempt(marker(folder, 1, "finished"), delivery_id, 1, outcome, lease_cancelled=scenario == "cancellation")

    terminal = FAILED if scenario == "rejection" else SENT
    if terminal == SENT:
        after_unknown = stand.snapshot(campaign_id)
        save(folder / "after-unknown.json", after_unknown)
        require(after_unknown["campaign_status"] == CAMPAIGN_RUNNING
                and delivery(after_unknown)["delivery_status"] in {WAITING, IN_PROGRESS}
                and delivery(after_unknown)["completed_at"] is None,
                "Unknown result became terminal")
        require(all(row[1] in {WAITING, IN_PROGRESS} for row in journal(after_unknown)),
                "Unknown result wrote a terminal journal entry")

        expected_journal.extend([(IN_PROGRESS, WAITING, None), (WAITING, IN_PROGRESS, None)])

        def released():
            snapshot = stand.snapshot(campaign_id)
            return snapshot if delivery(snapshot)["delivery_status"] == WAITING and len(snapshot["journal"]) >= 2 else None

        recovered = wait_for(released, f"{scenario}: Waiting with recovery journal")
        save(folder / "recovered.json", recovered)
        released_row = delivery(recovered)
        require(recovered["campaign_status"] == CAMPAIGN_RUNNING
                and released_row["id"] == delivery_id and released_row["worker_id"] is None
                and released_row["lock_expires_at"] is None and released_row["completed_at"] is None,
                "Recovery did not release the original delivery")
        require(datetime.fromisoformat(released_row["updated_at"]) >= datetime.fromisoformat(original["lock_expires_at"]),
                "Recovery ran before the original lease expired")
        require(journal(recovered) == expected_journal[:2], "Unexpected recovery journal")

        (folder / "allow-recapture").touch()
        check_attempt(marker(folder, 2, "entered"), delivery_id, 2)
        retry = stand.snapshot(campaign_id)
        save(folder / "recaptured.json", retry)
        check_active_lease(retry, worker)
        require(datetime.fromisoformat(delivery(retry)["lock_expires_at"]) > datetime.fromisoformat(original["lock_expires_at"]),
                "Retry did not acquire a fresh lease")
        require(journal(retry) == expected_journal, "Unexpected retry journal")
        (folder / "allow-attempt-2").touch()
        check_attempt(marker(folder, 2, "finished"), delivery_id, 2, "accepted")
        if mode == "http":
            check_wire(folder, scenario, delivery_id)

    def completed():
        snapshot = stand.snapshot(campaign_id)
        return snapshot if snapshot["campaign_status"] == COMPLETED else None

    wait_for(completed, f"{scenario}: campaign completion")
    stand.command(["docker", "stop", "--time", "10", worker])
    final = stand.snapshot(campaign_id)
    save(folder / "final.json", final)
    operator_result = "AcceptedByProvider" if terminal == SENT else "SimulatedOperatorRejected"
    expected_journal.append((IN_PROGRESS, terminal, operator_result))
    result = delivery(final)
    require(final["campaign_status"] == COMPLETED and result["id"] == delivery_id and result["delivery_status"] == terminal
            and result["worker_id"] is None and result["lock_expires_at"] is None and result["completed_at"] is not None,
            "Unexpected terminal delivery")
    require(journal(final) == expected_journal
            and all(row["delivery_id"] == delivery_id
                    and row["worker_id"] == (None if row["new_status"] == WAITING else worker)
                    for row in final["journal"]),
            "Unexpected terminal journal")
    last_attempt = 1 if terminal == FAILED else 2
    expected_markers = {f"attempt-{attempt}-{phase}.json" for attempt in range(1, last_attempt + 1)
                        for phase in ("entered", "finished")}
    require({path.name for path in folder.glob("attempt-*.json")} == expected_markers,
            "Unexpected set of operator attempts")
    print(f"PASS {scenario}: {outcome}, {operator_result}, campaign Completed", flush=True)
    return {"mode": mode, "first_outcome": outcome, "recovered": terminal == SENT,
            "terminal": "Sent" if terminal == SENT else "Failed", "campaign": "Completed"}


def run_a(stand, report):
    for scenario, mode, outcome in SCENARIOS:
        report["current_case"] = scenario
        report["cases"][scenario] = run_case(stand, scenario, mode, outcome)
    require(len(report["cases"]) == 5, "Expected four unknown outcomes and one confirmed rejection")
    report.pop("current_case")
    report.update(false_sent=0, false_failed=0)


def run_stand(suite, run_cases):
    def git(*args):
        return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()

    require(not git("status", "--porcelain"), "Use a clean checkout to produce reproducible evidence")
    require(hasattr(tarfile, "data_filter"), "Python with tarfile.data_filter is required")
    commit = git("rev-parse", "HEAD")
    run_id = uuid4().hex[:12]
    output = ROOT / "artifacts" / "acceptance" / suite / run_id
    (output / "control").mkdir(parents=True)
    report = {"commit": commit, "started_utc": datetime.now(timezone.utc).isoformat(),
              "python": platform.python_version(), "cases": {}}
    print(f"Evidence: {output}", flush=True)

    stand = None
    try:
        with tempfile.TemporaryDirectory(prefix="packcall-sm6-") as tmp:
            context = Path(tmp)
            archive = context / "source.tar"
            subprocess.run(["git", "archive", "--format=tar", "--output", str(archive), commit,
                            "src", "config/appsettings.packcall.json", "tests/PackCall.Workers.Acceptance", HERE.as_posix(),
                            "tests/acceptance/workers/coordination/Dockerfile",
                            "tests/acceptance/workers/coordination/fixtures/clock.c"], cwd=ROOT, check=True)
            with tarfile.open(archive) as source:
                source.extractall(context, filter="data")
            archive.unlink()
            stand = Stand(context, output, f"packcall-sm6-{run_id}")
            try:
                report["docker"] = stand.command(["docker", "version", "--format", "{{.Server.Version}}"])
                report["compose"] = stand.command(["docker", "compose", "version", "--short"])
                stand.start()
                report["postgres"] = stand.sql("SHOW server_version")
                run_cases(stand, report)
            except Exception:
                if log_error := stand.collect_logs():
                    report["log_error"] = log_error
                raise
            finally:
                try:
                    stand.stop()
                    report["cleanup"] = "passed"
                except Exception as error:
                    report["cleanup"] = stand.redact(str(error))
            require(report["cleanup"] == "passed", report["cleanup"])
        report.update(result="passed")
    except Exception as error:
        report.update(result="failed", error=stand.redact(str(error)) if stand else str(error))
        raise RuntimeError(report["error"]) from None
    finally:
        save(output / "results.json", report)


if __name__ == "__main__":
    run_stand("sm6-a", run_a)
