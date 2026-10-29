"""Read immutable JSONL prefixes; never repair or rewrite source files."""
import hashlib
import json
from pathlib import Path
from contract import ContractError, validate


def read_prefix(paths, kind="interval"):
    records, seen, last, quality = [], {}, {}, set()
    for path in sorted(map(Path, paths)):
        checksum_file = path.parent / ("runtime-checksums.json" if path.name.startswith("runtime-") else "checksums.json")
        if checksum_file.exists():
            try:
                expected = json.loads(checksum_file.read_text(encoding="utf-8")).get(path.name)
            except (ValueError, OSError):
                raise ContractError("invalid_checksums") from None
            if expected:
                with path.open("rb") as stream:
                    actual = hashlib.file_digest(stream, "sha256").hexdigest()
                if actual != expected:
                    raise ContractError("checksum_mismatch")
        with path.open("rb") as stream:
            for raw in stream:
                if not raw.endswith(b"\n"):
                    quality.add("truncated_tail")
                    break
                try:
                    record = validate(kind, json.loads(raw))
                except ContractError:
                    raise
                except (ValueError, UnicodeDecodeError):
                    raise ContractError("corrupt_complete_record") from None
                key = (record["session_id"], record["instance_id"], record["sequence"])
                canonical = json.dumps(record, sort_keys=True, separators=(",", ":"))
                if key in seen:
                    if seen[key] != canonical:
                        raise ContractError("conflicting_sequence")
                    quality.add("duplicate")
                    continue
                seen[key] = canonical
                instance = key[:2]
                if instance in last:
                    previous = last[instance]
                    if record["sequence"] <= previous["sequence"]:
                        raise ContractError("sequence_reset_within_instance")
                    if record["sequence"] != previous["sequence"] + 1 or record["elapsed_start_s"] > previous["elapsed_end_s"] + 1e-6:
                        quality.add("gap")
                    if record["elapsed_start_s"] < previous["elapsed_end_s"] - 1e-6:
                        raise ContractError("overlapping_windows")
                    for name, metric in record["metrics"].items():
                        earlier = previous["metrics"].get(name)
                        if metric["kind"] == "cumulative" and earlier and metric["value"] is not None and earlier["value"] is not None and metric["value"] < earlier["value"]:
                            quality.add("counter_reset")
                elif record["sequence"] != 0:
                    quality.add("missing_prefix")
                if record["quality"] != "complete":
                    quality.add(record["quality"])
                last[instance] = record
                records.append(record)
    return {"records": records, "quality": sorted(quality), "last_boundaries": [
        {"session_id": key[0], "instance_id": key[1], "sequence": value["sequence"], "elapsed_end_s": value["elapsed_end_s"]}
        for key, value in sorted(last.items())]}


def read_session(directory):
    root = Path(directory)
    result = read_prefix(root.glob("processes/*/intervals-*.jsonl"))
    quality = set(result["quality"])
    for process in root.glob("processes/*/process.json"):
        marker = process.parent / "stopped.json"
        if not marker.exists():
            quality.add("process_without_stop_marker")
        else:
            try:
                stopped = json.loads(marker.read_text())
                if stopped.get("telemetry_status") != "complete":
                    quality.add("partial")
            except (ValueError, OSError):
                raise ContractError("invalid_stop_marker") from None
    if not result["records"]:
        quality.add("missing_intervals")
    result["quality"] = sorted(quality)
    return result
