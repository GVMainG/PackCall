"""Version 1 file contract. Validation errors never echo untrusted values."""
import json
import math
from datetime import datetime
from pathlib import Path
from jsonschema import Draft202012Validator, FormatChecker

ROOT = Path(__file__).resolve().parent
VERSION = 1


class ContractError(ValueError):
    pass


def bounds():
    result = [0.0]
    k = 0
    while result[-1] < 600000:
        result.append(0.01 * 1.1 ** k)
        k += 1
    return result


def validate(kind, document):
    if kind not in {"manifest", "interval", "sample", "lifecycle", "metric-definitions"}:
        raise ContractError("unknown_document_kind")
    if isinstance(document, dict) and "schema_version" in document and document["schema_version"] != VERSION:
        raise ContractError("incompatible_schema_version")
    schema = json.loads((ROOT / "schemas" / f"{kind}.v1.json").read_text(encoding="utf-8"))
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    if not validator.is_valid(document):
        raise ContractError("invalid_document_schema")
    # JSON Schema does not express these relationships between fields.
    if kind in {"interval", "sample", "lifecycle"}:
        if document["elapsed_end_s"] < document["elapsed_start_s"] or not math.isclose(
            document["duration_s"], document["elapsed_end_s"] - document["elapsed_start_s"], abs_tol=1e-6
        ):
            raise ContractError("invalid_time_bounds")
        if datetime.fromisoformat(document["end_utc"].replace("Z", "+00:00")) < datetime.fromisoformat(document["start_utc"].replace("Z", "+00:00")) and document["quality"] != "clock_jump":
            raise ContractError("unmarked_clock_jump")
        for name, metric in document["metrics"].items():
            if name.startswith("terminal.") and metric["unit"] != "deliveries":
                raise ContractError("invalid_metric_unit")
            if metric["kind"] != "histogram" and ((metric["value"] is None) != (metric["missing_reason"] is not None)):
                raise ContractError("invalid_missing_value")
            values = metric.get("buckets", [])
            if metric["kind"] == "histogram":
                if metric["bounds_ms"] != bounds() or len(values) != len(bounds()) + 1:
                    raise ContractError("incompatible_histogram_bounds")
                if sum(values) != metric["count"]:
                    raise ContractError("invalid_histogram_count")
                if metric["count"] == 0:
                    if metric["min"] is not None or metric["max"] is not None or metric["sum"] != 0:
                        raise ContractError("invalid_empty_histogram")
                elif metric["min"] is None or metric["max"] is None or not (
                    metric["min"] <= metric["sum"] / metric["count"] <= metric["max"]
                ):
                    raise ContractError("invalid_histogram_range")
    def finite(value):
        if isinstance(value, float) and not math.isfinite(value):
            raise ContractError("non_finite_number")
        if isinstance(value, dict):
            for child in value.values():
                finite(child)
        elif isinstance(value, list):
            for child in value:
                finite(child)
    finite(document)
    return document


def read(kind, path):
    try:
        document = json.loads(Path(path).read_text(encoding="utf-8"))
    except (ValueError, OSError):
        raise ContractError("unreadable_document") from None
    return validate(kind, document)
