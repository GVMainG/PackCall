"""Run SM-6 B and C against the same delivery in an isolated Compose project."""
import json
from datetime import datetime
from uuid import UUID

from run import (
    CAMPAIGN_RUNNING, COMPLETED, IN_PROGRESS, SENT, WAITING, check_active_lease, check_attempt,
    delivery, journal, marker, read_json, require, run_stand, save, wait_for,
)


def snapshot(stand, campaign_id, folder, name):
    state = stand.snapshot(campaign_id)
    save(folder / f"{name}.json", state)
    return state


def process(stand, worker):
    value = stand.command(["docker", "inspect", "--format", "{{.Id}} {{json .State}}", worker])
    container_id, state = value.split(" ", 1)
    return {"container_id": container_id, "worker": worker, "state": json.loads(state)}


def same_state(before, after):
    for key in ("deliveries", "journal", "campaign_status"):
        require(before[key] == after[key], f"{key} changed while no write was expected")


def assert_journal(state, delivery_id, transitions, workers):
    require(journal(state) == transitions, "Unexpected status journal")
    require(all(row["delivery_id"] == delivery_id for row in state["journal"])
            and [row["worker_id"] for row in state["journal"]] == workers,
            "Unexpected delivery or Worker in the journal")


def run_cases(stand, report):
    control = stand.output / "control"
    original_folder = control / "worker-loss"
    recovery_folder = control / "recovery"
    stale_folder = control / "stale-result"
    for folder in (original_folder, recovery_folder, stale_folder):
        folder.mkdir()
        (folder / "allow-attempt-1").touch()

    report["current_case"] = "B"
    campaign_id = UUID(stand.api("campaigns", {
        "name": "SM-6 B and C", "messageText": "Worker loss and stale lease",
    })["id"])
    stand.api(f"campaigns/{campaign_id}/start", {})
    report["campaign_id"] = str(campaign_id)

    original_worker = stand.start_probe("worker-loss", "-d")
    accepted = marker(original_folder, 1, "finished")
    held = snapshot(stand, campaign_id, original_folder, "result-held")
    original_row = delivery(held)
    delivery_id = original_row["id"]
    check_attempt(accepted, delivery_id, 1, "accepted")
    check_active_lease(held, original_worker)
    expiry = datetime.fromisoformat(original_row["lock_expires_at"])
    transitions = [(WAITING, IN_PROGRESS, None)]
    workers = [original_worker]
    assert_journal(held, delivery_id, transitions, workers)

    stand.command(["docker", "kill", "--signal", "SIGKILL", original_worker])
    killed = process(stand, original_worker)
    save(original_folder / "killed.json", killed)
    require(not killed["state"]["Running"] and killed["state"]["ExitCode"] == 137
            and not killed["state"]["OOMKilled"], "Original Worker did not exit after SIGKILL")

    successor = stand.start_probe("recovery", "-d")
    first_pass_path = recovery_folder / "first-recovery-pass.json"
    first_pass = wait_for(lambda: read_json(first_pass_path) if first_pass_path.exists() else None,
                          "Successor's first completed recovery pass")
    before_expiry = snapshot(stand, campaign_id, original_folder, "killed-before-expiry")
    require(first_pass == {"released": 0}, "Successor recovered a delivery on its first pass")
    check_active_lease(before_expiry, original_worker)
    same_state(held, before_expiry)

    captured = read_json(original_folder / "captured-delivery.json")
    require(captured["Id"] == delivery_id and captured["CampaignId"] == str(campaign_id)
            and captured["WorkerId"] == original_worker and captured["DeliveryStatus"] == IN_PROGRESS
            and datetime.fromisoformat(captured["LockExpiresAt"].replace("Z", "+00:00")) == expiry,
            "Original Worker snapshot does not match the original lease")

    def recovered_waiting():
        state = stand.snapshot(campaign_id)
        return state if delivery(state)["delivery_status"] == WAITING and len(state["journal"]) >= 2 else None

    recovered = wait_for(recovered_waiting, "Waiting delivery with recovery journal")
    save(recovery_folder / "waiting.json", recovered)
    row = delivery(recovered)
    require(recovered["campaign_status"] == CAMPAIGN_RUNNING and row["id"] == delivery_id
            and row["worker_id"] is None and row["lock_expires_at"] is None and row["completed_at"] is None,
            "Recovery did not release the original delivery")
    transitions.append((IN_PROGRESS, WAITING, None))
    workers.append(None)
    assert_journal(recovered, delivery_id, transitions, workers)
    require(datetime.fromisoformat(recovered["db_now"]) >= expiry
            and datetime.fromisoformat(row["updated_at"]) >= expiry
            and datetime.fromisoformat(recovered["journal"][-1]["occurred_at"]) >= expiry,
            "Recovery occurred before expiry")

    (recovery_folder / "allow-recapture").touch()
    check_attempt(marker(recovery_folder, 1, "finished"), delivery_id, 1, "accepted")
    before_stale = snapshot(stand, campaign_id, recovery_folder, "recaptured-before-stale")
    check_active_lease(before_stale, successor)
    successor_row = delivery(before_stale)
    require(successor_row["id"] == delivery_id
            and datetime.fromisoformat(successor_row["lock_expires_at"]) > expiry,
            "Successor did not acquire a fresh lease for the original delivery")
    transitions.append((WAITING, IN_PROGRESS, None))
    workers.append(successor)
    assert_journal(before_stale, delivery_id, transitions, workers)
    report["cases"]["B"] = {"result": "passed", "killed_exit_code": 137, "expired": True,
                            "released_before_expiry": first_pass["released"],
                            "recovered_status": "Waiting", "recaptured_by": successor}
    print("PASS B: Worker killed, PostgreSQL lease expired, delivery recovered and recaptured", flush=True)

    report["current_case"] = "C"
    stale_worker = stand.start_probe("stale-result", "-d", "-e",
                                     "STALE_FILE=/control/worker-loss/captured-delivery.json")
    stand.command(["docker", "wait", stale_worker], timeout=40)
    stale_process = process(stand, stale_worker)
    save(stale_folder / "process.json", stale_process)
    after_stale = snapshot(stand, campaign_id, stale_folder, "after")
    exit_code = stale_process["state"]["ExitCode"]
    require(exit_code == 0, f"One-shot stale Worker exited with code {exit_code}")
    require(not stale_process["state"]["Running"]
            and stale_process["container_id"] != killed["container_id"],
            "Stale snapshot was not processed by a separate one-shot Worker process")
    check_attempt(marker(stale_folder, 1, "finished"), delivery_id, 1, "accepted")
    processing = read_json(stale_folder / "processing.json")
    require(processing["deliveryId"] == delivery_id and processing["outcome"] == "StaleResult",
            "ProcessAsync did not observe rejection by TryCompleteAsync")
    require(processing["affectedRows"] == [0], "Expected one PostgreSQL delivery UPDATE affecting zero rows")
    check_active_lease(after_stale, successor)
    same_state(before_stale, after_stale)

    (recovery_folder / "allow-result").touch()

    def completed():
        state = stand.snapshot(campaign_id)
        return state if state["campaign_status"] == COMPLETED else None

    wait_for(completed, "Campaign completion after release of the successor's result")
    report["worker_runtime"] = stand.command(["docker", "exec", successor, "dotnet", "--list-runtimes"])
    stand.command(["docker", "stop", "--time", "10", successor])
    final = snapshot(stand, campaign_id, recovery_folder, "final")
    final_row = delivery(final)
    require(final["campaign_status"] == COMPLETED and final_row["id"] == delivery_id
            and final_row["delivery_status"] == SENT and final_row["completed_at"] is not None
            and final_row["worker_id"] is None and final_row["lock_expires_at"] is None,
            "Unexpected terminal delivery")
    transitions.append((IN_PROGRESS, SENT, "AcceptedByProvider"))
    workers.append(successor)
    assert_journal(final, delivery_id, transitions, workers)
    for folder in (original_folder, recovery_folder, stale_folder):
        require({path.name for path in folder.glob("attempt-*.json")}
                == {"attempt-1-entered.json", "attempt-1-finished.json"},
                f"Unexpected operator attempts in {folder.name}")
    report["cases"]["C"] = {"result": "passed", "processing_outcome": "StaleResult",
                            "update_affected_rows": processing["affectedRows"],
                            "row_and_journal_unchanged": True, "terminal_entries": 1, "campaign": "Completed"}
    report.pop("current_case")
    print("PASS C: stale result rejected, row and journal unchanged, one terminal entry, campaign Completed", flush=True)


if __name__ == "__main__":
    run_stand("sm6-bc", run_cases)
