import json
from argparse import Namespace
from pathlib import Path
import subprocess
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "scripts/performance"))
import baseline
import session
from baseline import (EXTERNAL_CORE_METRICS, assess, latency_in_budget, pooled_p95, terminal_counts,
                      throughput, throughput_gate, validate_external, verify_checksums)
from collectors import CollectionError
from run import compose_output, verify_privacy
from session import atomic, compose_profile, finalize_artifacts


def test_throughput_uses_response_and_conservative_previous_request():
    previous = dict(request_s=19, response_s=19.3, sent=298_000, campaign_status=1)
    confirmed = dict(request_s=20, response_s=20.3, sent=300_000, campaign_status=2)
    result = throughput(300_000, 10, dict(previous=previous, confirmed=confirmed))
    assert result == pytest.approx(dict(duration_s=10.3, uncertainty_s=1.3,
                                       deliveries_per_s=29_126.213592, upper_deliveries_per_s=33_333.333333))


def test_throughput_requires_previous_incomplete_poll():
    with pytest.raises(CollectionError, match="missing_incomplete_poll"):
        throughput(300_000, 10, dict(previous=None, confirmed={}))


@pytest.mark.parametrize(("off", "on", "status"), [
    ([100, 100, 100], [96, 97, 98], "pass"),
    ([100, 100, 100], [94, 94, 94], "over_budget"),
    ([100, 100, 100], [94.8, 94.8, 94.8], "inconclusive"),
    ([100, 100, 106], [98, 98, 104], "inconclusive"),
], ids=["pass", "loss-exceeds-budget", "uncertainty-crosses-budget", "off-spread-exceeds-budget"])
def test_throughput_gate_pairs_and_brackets(off, on, status):
    pairs = [{"off": dict(deliveries_per_s=a, upper_deliveries_per_s=a+.5),
              "on": dict(deliveries_per_s=b, upper_deliveries_per_s=b+.5)} for a, b in zip(off, on)]
    result = throughput_gate(pairs)
    assert result["status"] == status
    assert result["paired_loss_pct"] == pytest.approx([100*(1-b/a) for a, b in zip(off, on)])
    lower, upper = result["median_loss_bounds_pct"]
    assert lower < result["median_loss_pct"] < upper


def test_throughput_gate_rejects_two_pairs():
    with pytest.raises(CollectionError, match="incomplete_throughput_series"):
        throughput_gate([{}, {}])


def test_off_spread_upper_cannot_use_both_endpoints_of_one_arm():
    pairs = [{"off": dict(deliveries_per_s=low, upper_deliveries_per_s=high),
              "on": dict(deliveries_per_s=100, upper_deliveries_per_s=100)}
             for low, high in [(97, 103), (100, 100), (101, 101)]]
    result = throughput_gate(pairs)
    assert result["off_spread_upper_pct"] == pytest.approx(4)
    assert result["status"] == "pass"


def test_latency_gate_includes_exact_ten_percent():
    assert latency_in_budget(100, 110)
    assert not latency_in_budget(100, 110.001)


def test_terminal_counters_require_explicit_zero():
    totals = {"sent": 0, "failed": 0}
    metrics = {f"terminal.{status}.{kind}": {"value": 0}
               for status in totals for kind in ("delta", "cumulative")}
    terminal_counts(metrics, totals)
    del metrics["terminal.failed.cumulative"]
    with pytest.raises(CollectionError, match="missing_terminal_counters"):
        terminal_counts(metrics, totals)


@pytest.mark.parametrize("case", ["late-finish", "unavailable-only", "before-start", "clock-jump"])
def test_external_coverage_requires_usable_sample_start_in_window(tmp_path, case):
    from contract import ROOT, read
    from collectors import scalar
    template = read("sample", ROOT / "examples/sample.json")
    samples = tmp_path / "samples"
    samples.mkdir()
    for source, names in EXTERNAL_CORE_METRICS.items():
        missing = dict(template, source=source, quality="missing", metrics={"collector.available": scalar(None)},
                       elapsed_start_s=10, elapsed_end_s=10, duration_s=0)
        valid = dict(template, source=source, sequence=1, elapsed_start_s=11, elapsed_end_s=14, duration_s=3,
                     metrics={name: scalar(0) for name in names})
        rows = [missing, valid]
        if source == "host":
            if case == "unavailable-only":
                rows = [missing, dict(valid, metrics={name: scalar(None) for name in names})]
            elif case == "before-start":
                rows = [dict(valid, sequence=0, elapsed_start_s=8, elapsed_end_s=11)]
            elif case == "clock-jump":
                rows = [missing, dict(valid, quality="clock_jump")]
        (samples / f"{source}.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    if case == "late-finish":
        validate_external(tmp_path, template["session_id"], 10, 12)
    else:
        reason = "invalid_external_samples" if case == "clock-jump" else "missing_processing_samples_host"
        with pytest.raises(CollectionError, match=reason):
            validate_external(tmp_path, template["session_id"], 10, 12)


def write_probe(directory, role, values, **overrides):
    row = dict(schema_version=1, capacity=300_000, count=len(values), overflow=0, invalid=0, durations_ms=values)
    atomic(directory / f"latency-{role}.json", row | overrides)


def test_p95_pools_samples_before_nearest_rank(tmp_path):
    write_probe(tmp_path, "worker0", list(range(1, 20)))
    write_probe(tmp_path, "worker1", [1000])
    assert pooled_p95(tmp_path, {"worker0": {"sent": 19}, "worker1": {"sent": 1}}, 20) == 19


@pytest.mark.parametrize("overrides", [{"overflow": 1}, {"invalid": 1}, {"count": 2}],
                         ids=["overflow", "invalid-sample", "count-disagrees-with-journal"])
def test_p95_rejects_incomplete_probe(tmp_path, overrides):
    write_probe(tmp_path, "worker0", [10], **overrides)
    with pytest.raises(CollectionError, match="invalid_latency_probe"):
        pooled_p95(tmp_path, {"worker0": {"sent": 1}}, 1)


def test_missing_telemetry_cannot_pass_session_gate(tmp_path):
    from baseline import PRODUCT_SHA
    from contract import ROOT
    manifest = json.loads((ROOT / "examples/manifest.json").read_text(encoding="utf-8"))
    manifest.update(commit=PRODUCT_SHA, dirty=False, workload_status="completed", telemetry_status="partial")
    atomic(tmp_path / "manifest.json", manifest)
    with pytest.raises(CollectionError, match="incomplete_session"):
        assess(tmp_path, 3, {}, {})


def test_checksums_must_cover_all_persisted_records(tmp_path):
    atomic(tmp_path / "checksums.json", {"schema_version": 1, "files": {}})
    atomic(tmp_path / "final.json", {"schema_version": 1})
    with pytest.raises(CollectionError, match="incomplete_checksums"):
        verify_checksums(tmp_path)


def test_finalize_includes_nested_checksum_indexes(tmp_path):
    nested = tmp_path / "processes" / "worker" / "checksums.json"
    atomic(nested, {})
    finalize_artifacts(tmp_path)
    verify_checksums(tmp_path)
    assert json.loads((tmp_path / "checksums.json").read_text())["files"].keys() == {
        "processes/worker/checksums.json"}


def test_compose_failure_suppresses_raw_diagnostics(monkeypatch):
    canary = "raw-operator-error password=synthetic"
    monkeypatch.setattr(subprocess, "run", lambda *args, **kwargs: subprocess.CompletedProcess([], 1, canary, canary))
    with pytest.raises(CollectionError) as failure:
        compose_output(["up"])
    assert str(failure.value) == "compose_command_failed"
    assert canary not in repr(failure.value)


@pytest.mark.parametrize("canary", [b"perf000000001@example.invalid", b"x"*64, b"synthetic-session-token",
                                  b"Traceback (most recent call last)"],
                         ids=["address", "message", "secret", "traceback"])
def test_privacy_scan_rejects_canary_in_error_artifact(tmp_path, canary):
    (tmp_path / "failure.json").write_bytes(b'{"unexpected":"' + canary + b'"}')
    with pytest.raises(CollectionError, match="privacy_canary_detected") as failure:
        verify_privacy(tmp_path, b"synthetic-session-token")
    assert canary.decode() not in str(failure.value)


def test_off_disables_diagnostics_and_latency_hooks_are_worker_only(tmp_path):
    pinned = {role: role + "-image-id" for role in ("worker", "api", "migrator", "db", "http")}
    services = compose_profile("session", tmp_path, 2, False, images=pinned,
                               latency_probe=tmp_path/"LatencyProbe.dll")["services"]
    assert all("build" not in item and item["pull_policy"] == "never" for item in services.values())
    assert services["worker0"]["image"] == services["worker1"]["image"] == pinned["worker"]
    for role in ("worker0", "worker1", "api"):
        assert services[role]["environment"]["PerformanceMeasurement__Enabled"] == "false"
    assert {role for role, service in services.items() if "DOTNET_STARTUP_HOOKS" in service.get("environment", {})} == {
        "worker0", "worker1"}


@pytest.mark.parametrize("foreign_network", [False, True], ids=["retry", "foreign-network"])
def test_cleanup_skips_absent_resources_and_validates_before_removing(tmp_path, monkeypatch, foreign_network):
    sid = "00000000-0000-4000-8000-000000000001"
    project = "packcall-perf-" + sid.replace("-", "")
    volume, network = project + "_data", project + "_default"
    directory = tmp_path / sid
    atomic(directory / "session.json", dict(session_id=sid, project=project, volume=volume,
                                           containers={"worker0": "present", "worker1": "gone"}))
    removed = []
    network_failure = not foreign_network

    def docker(argv, **kwargs):
        nonlocal network_failure
        if argv[1:3] == ["container", "ls"]:
            return "" if "present" in removed else "present\n"
        if argv[1:3] == ["volume", "ls"]:
            return "" if volume in removed else volume + "\n"
        if argv[1:3] == ["network", "ls"]:
            return "" if network in removed else network + "\n"
        if argv[1:2] == ["inspect"] or argv[1:3] == ["volume", "inspect"]:
            assert argv[-1] in {"present", volume}
            return sid
        if argv[1:3] == ["network", "inspect"]:
            return "foreign" if foreign_network else project
        if argv[1] == "rm" or argv[2] == "rm":
            if argv[-1] == network and network_failure:
                network_failure = False
                raise CollectionError("network_busy")
            removed.append(argv[-1])
            return ""
        pytest.fail("unexpected Docker command")

    monkeypatch.setattr(session, "command", docker)
    if foreign_network:
        with pytest.raises(CollectionError, match="invalid_network_ownership"):
            session.cleanup_owned(directory, network=True)
        assert removed == []
    else:
        with pytest.raises(CollectionError, match="network_busy"):
            session.cleanup_owned(directory, network=True)
        session.cleanup_owned(directory, network=True)
        session.cleanup_owned(directory, network=True)
        assert removed == ["present", volume, network]
        assert json.loads((directory / "cleanup.json").read_text())["resources_removed"] is True


@pytest.mark.parametrize("failure", ["interrupted", "run_failed"])
def test_series_records_interrupt_or_original_failure(tmp_path, monkeypatch, failure):
    for name in ("preflight", "prepare_images", "tool_revision"):
        monkeypatch.setattr(baseline, name, lambda *args: {})
    def command(argv, **kwargs):
        if argv[0] == "dotnet":
            raise KeyboardInterrupt
        return "test"
    monkeypatch.setattr(baseline, "command", command)
    def run_arm(args, **kwargs):
        directory = args.output / str(len(list(args.output.iterdir())))
        directory.mkdir()
        if failure == "run_failed":
            raise CollectionError("run_failed")
        return directory
    monkeypatch.setattr(baseline, "run", run_arm)
    monkeypatch.setattr(baseline, "assess", lambda *args: ({}, {}))
    def cleanup(*args, **kwargs):
        if failure == "run_failed":
            raise CollectionError("cleanup_failed")
    monkeypatch.setattr(baseline, "cleanup_owned", cleanup)
    output = tmp_path / "output"
    args = Namespace(product_root=tmp_path / "product", output=output, smoke=True)
    if failure == "interrupted":
        assert baseline.experiment(args) == "interrupted"
    else:
        with pytest.raises(CollectionError, match="run_failed"):
            baseline.experiment(args)
    report = json.loads(next(output.glob("*/series.json")).read_text())
    if failure == "interrupted":
        assert report["status"] == "interrupted"
    else:
        assert report["status"] == "invalid" and report["failure_code"] == "run_failed"
        assert report["sessions"][0]["cleanup"] == "failed"
