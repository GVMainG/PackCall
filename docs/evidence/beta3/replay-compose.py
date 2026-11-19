import json
import os
import subprocess
import tarfile
import time
import uuid
from pathlib import Path
from urllib.error import URLError
from urllib.request import Request, urlopen

project = "packcall-s219-" + uuid.uuid4().hex[:12]
sha = "6209783f4b5d68babe342b4eeae9eec6eeef91bc"
out = (Path("artifacts/story-2-19/compose-replay") / project).resolve()
out.mkdir(parents=True)

env = os.environ.copy()
for key in ("PACKCALL_ENVIRONMENT", "TEST_SENDER_MODE", "TEST_SIMULATE_ERRORS",
            "TEST_OPERATOR_URL", "TEST_OPERATOR_TIMEOUT_SECONDS", "POSTGRES_DB",
            "POSTGRES_USER", "POSTGRES_PASSWORD"):
    env.pop(key, None)
env["DOCKER_REGISTRY"] = project + "/"
(out / "smoke.override.yml").write_text("""services:
  packcall.api:
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_HTTP_PORTS: 8080
    ports:
      - "127.0.0.1::8080"
""", encoding="utf-8")
cli = ["docker", "compose", "--env-file", "docker-compose.env", "-p", project,
       "-f", "docker-compose.yml", "-f", str(out / "smoke.override.yml")]

def compose(*args, check=True):
    return subprocess.run(cli + list(args), env=env, check=check,
                          text=True, encoding="utf-8", errors="replace",
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)

def save(name, value):
    (out / name).write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

def api(path, data=None, method=None):
    path = base + path if path.startswith("/") else path
    req = Request(path, json.dumps(data).encode() if data is not None else None,
                  headers={"Content-Type": "application/json"}, method=method)
    with urlopen(req, timeout=5) as response:
        return response.status, dict(response.headers), json.load(response)

def sql(query):
    result = compose("exec", "-T", "packcall.db", "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                     "-U", "packcall", "-d", "packcall", "-c", query)
    return result.stdout.strip()

try:
    tree = subprocess.check_output(["git", "status", "--porcelain"], text=True).strip()
    save("source.json", {"sha": sha, "gitStatus": tree, "project": project})
    archive = out / "source.tar"
    source = out / "source"
    subprocess.run(["git", "archive", "--format=tar", "-o", str(archive), sha], check=True)
    with tarfile.open(archive) as source_tar:
        source_tar.extractall(source, filter="data")
    os.chdir(source)
    up = compose("up", "-d", "--build", check=False)
    (out / "compose-up.log").write_text(up.stdout, encoding="utf-8")
    up.check_returncode()
    ps = compose("ps", "--all", "--format", "json").stdout
    (out / "compose-ps.jsonl").write_text(ps, encoding="utf-8")
    entries = [json.loads(line) for line in ps.splitlines()]
    assert any(e["Service"] == "packcall.migrator" and e["State"] == "exited" and e["ExitCode"] == 0 for e in entries), entries
    assert all(any(e["Service"] == s and e["State"] == "running" for e in entries)
               for s in ("packcall.api", "packcall.worker", "packcall.db")), entries
    base = "http://" + compose("port", "packcall.api", "8080").stdout.strip()
    deadline = time.monotonic() + 60
    while True:
        try:
            _, _, contacts = api("/api/contacts?pageSize=1")
            break
        except (URLError, ConnectionError, TimeoutError):
            if time.monotonic() >= deadline:
                raise
            time.sleep(1)
    assert contacts["totalCount"] == 0, contacts
    status, _, schema = api("/openapi/v1.json")
    assert status == 200
    save("openapi.runtime.json", schema)
    status, headers, contact = api("/api/contacts",
        {"displayName": "Beta.3 smoke", "channels": [{"channel": "email", "address": "beta3-smoke@example.test"}]})
    assert status == 201 and headers.get("Location"), (status, headers)
    read_status, _, read_contact = api(headers["Location"])
    assert read_status == 200 and read_contact["id"] == contact["id"], read_contact
    assert [(c["channel"], c["address"]) for c in read_contact["channels"]] == [("email", "beta3-smoke@example.test")], read_contact
    status, headers, campaign = api("/api/campaigns",
        {"name": "Beta.3 smoke", "messageText": "Smoke", "selection": "contactIds",
         "contactIds": [contact["id"]], "channels": ["email"]})
    assert status == 201 and campaign["status"] == "Draft" and headers.get("Location"), campaign
    campaign_id = str(uuid.UUID(campaign["id"]))
    location = headers["Location"]
    status, _, started = api(f"/api/campaigns/{campaign_id}/start", method="POST")
    assert status == 200 and started["status"] in ("InProgress", "Completed"), started
    deadline = time.monotonic() + 60
    while True:
        _, _, details = api(location)
        if details["campaign"]["status"] == "Completed":
            break
        assert time.monotonic() < deadline, details
        time.sleep(1)
    _, _, deliveries = api(f"/api/campaigns/{campaign_id}/deliveries")
    items = deliveries["items"]
    assert len(items) == 1 and items[0]["status"] == "Sent", deliveries
    assert items[0]["recipientId"] == contact["id"], deliveries
    history = sql('SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"').splitlines()
    assert history[-1].endswith("_DeliveryCaptureIndex"), history
    pg_version = sql("SHOW server_version")
    journal = json.loads(sql(f"SELECT json_agg(j ORDER BY j.id) FROM delivery_status_journal j WHERE campaign_id = '{campaign_id}'"))
    assert [(j["previous_status"], j["new_status"], j["operator_result"]) for j in journal] == [
        (0, 1, None), (1, 2, "AcceptedByProvider")], journal
    save("results.json", {"sha": sha, "project": project, "initialContactCount": contacts["totalCount"],
         "postgres": pg_version, "migrations": history, "contact": contact, "createdCampaign": campaign,
         "startedCampaign": started, "details": details, "deliveries": deliveries, "journal": journal})
    print("Clean Compose smoke passed", project)
finally:
    try:
        (out / "compose-services.log").write_text(compose("logs", "--no-color", check=False).stdout, encoding="utf-8")
    finally:
        cleanup = compose("down", "--volumes", "--rmi", "local", check=False)
        (out / "compose-down.log").write_text(cleanup.stdout, encoding="utf-8")
        cleanup.check_returncode()
