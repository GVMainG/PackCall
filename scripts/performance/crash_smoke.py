"""SIGKILL smoke, independent of application DB and HTTP; never runs a workload."""
import json
from pathlib import Path
import subprocess
import time
import uuid
from reader import read_session, read_prefix

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent


def run():
    session = str(uuid.uuid4())
    output = REPO / "artifacts/performance" / session
    output.mkdir(parents=True)
    publish = REPO / "artifacts/performance-probe"
    result = subprocess.run(["dotnet", "publish", str(ROOT / "probe/PerformanceProbe.csproj"), "-c", "Release", "-o", str(publish), "--verbosity", "quiet"], capture_output=True, timeout=180)
    if result.returncode:
        raise RuntimeError("probe_build_failed")
    name = "packcall-perf-crash-" + uuid.uuid4().hex
    canary = "secret-payload-canary-" + uuid.uuid4().hex
    def docker(*args):
        result = subprocess.run(["docker", *args], text=True, capture_output=True, timeout=60)
        if result.returncode: raise RuntimeError("crash_fixture_command_failed")
        return result.stdout
    docker("run", "-d", "--name", name, "--label", "packcall.performance.fixture=true",
           "-v", f"{publish}:/app:ro", "-v", f"{output}:/output", "-e", "PERFORMANCE_SECRET_CANARY=" + canary,
           "mcr.microsoft.com/dotnet/aspnet:10.0", "dotnet", "/app/PerformanceProbe.dll", "/output", session)
    try:
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            files = list(output.glob("processes/*/intervals-*.jsonl"))
            if files and sum(path.read_bytes().count(b"\n") for path in files) >= 2: break
            time.sleep(.1)
        else: raise RuntimeError("probe_windows_timeout")
        prefix = {path: path.read_bytes() for path in files}
        docker("kill", "--signal", "KILL", name)
        result = read_session(output)
        runtime = read_prefix(output.glob("processes/*/runtime-*.jsonl"), "sample")
        assert runtime["records"]
        assert runtime["records"][0]["metrics"]["process.rss"]["unit"] == "bytes"
        assert runtime["records"][0]["metrics"]["runtime.exceptions"]["missing_reason"] == "unsupported"
        assert len(result["records"]) >= 2
        assert "process_without_stop_marker" in result["quality"]
        assert not list(output.glob("processes/*/stopped.json"))
        assert all(path.read_bytes().startswith(data) for path, data in prefix.items())
        assert not any(name.startswith("operator.call") for row in result["records"] for name in row["metrics"])
        assert all(canary.encode() not in path.read_bytes() for path in output.rglob("*") if path.is_file())
        (output / "smoke-result.json").write_text(json.dumps({"test": "sigkill_prefix", "status": "passed", "intervals": len(result["records"]), "quality": result["quality"]}, indent=2) + "\n")
        print("PASS: SIGKILL prefix, no fabricated in-flight duration, no completed marker, no canary; " + str(output))
    finally:
        docker("rm", "-f", name)


if __name__ == "__main__": run()
