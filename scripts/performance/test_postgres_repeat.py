"""Small isolated PostgreSQL fixture. Run explicitly; never connects to a dev database."""
import json
from pathlib import Path
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parent


def run():
    name = "packcall-perf-fixture-" + uuid.uuid4().hex
    def docker(*args, input=None):
        result = subprocess.run(["docker", *args], input=input, text=True, capture_output=True, timeout=60)
        if result.returncode:
            raise RuntimeError("fixture_command_failed")
        return result.stdout
    # Trust is restricted to an unpublished isolated test container; no host port or persistent volume.
    docker("run", "-d", "--name", name, "--label", "packcall.performance.fixture=true",
           "-e", "POSTGRES_HOST_AUTH_METHOD=trust", "postgres:18.6-alpine3.24")
    try:
        for _ in range(60):
            probe = subprocess.run(["docker", "exec", name, "pg_isready", "-U", "postgres"], capture_output=True, timeout=5)
            if probe.returncode == 0:
                break
            time.sleep(.5)
        else:
            raise RuntimeError("fixture_not_ready")
        campaign = str(uuid.uuid4())
        first, second, third = (str(uuid.uuid4()) for _ in range(3))
        sql = f"""
CREATE TABLE delivery_status_journal(campaign_id uuid, delivery_id uuid, previous_status integer, new_status integer);
INSERT INTO delivery_status_journal VALUES
('{campaign}', '{first}', 0, 1), ('{campaign}', '{first}', 1, 0), ('{campaign}', '{first}', 0, 1),
('{campaign}', '{second}', 0, 1), ('{campaign}', '{second}', 1, 0),
('{campaign}', '{third}', 1, 0);
"""
        docker("exec", "-i", name, "psql", "-U", "postgres", "-v", "ON_ERROR_STOP=1", input=sql)
        output = docker("exec", "-i", name, "psql", "-U", "postgres", "-qAt", "-v", "ON_ERROR_STOP=1",
                        "-v", "campaign_id=" + campaign, input=(ROOT / "sql/repeat_captures.sql").read_text())
        document = json.loads(output)
        assert document == {"observed_repeat_captures": 1, "journal_non_atomic": True, "quality": "lower_bound"}
        assert all(value not in output for value in (first, second, third, campaign))
        print("PASS: repeated capture=1; release without recapture=0; missing capture journal=lower_bound; aggregate-only export")
    finally:
        # Exact generated name is the only resource this fixture owns.
        docker("rm", "-f", name)


if __name__ == "__main__":
    run()
