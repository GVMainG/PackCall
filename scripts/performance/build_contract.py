"""Regenerate checked-in schemas and synthetic examples deterministically."""
import json
from contract import ROOT, bounds


def obj(properties, optional=()):
    return {"type": "object", "additionalProperties": False, "properties": properties,
            "required": [key for key in properties if key not in optional]}


def enum(*values):
    return {"enum": list(values)}


number = {"type": "number", "minimum": 0}
integer = {"type": "integer", "minimum": 0}
uuid = {"type": "string", "format": "uuid"}
utc = {"type": "string", "format": "date-time"}
code = {"type": "string", "pattern": "^[a-zA-Z0-9_.:-]{1,160}$"}
nullable_number = {"type": ["number", "null"], "minimum": 0}
units = enum("ms", "s", "deliveries", "events", "bytes", "cores", "threads", "connections", "deliveries/s")
quality = enum("complete", "partial", "missing", "clock_jump", "reset", "gap")
scalar = obj({"kind": enum("delta", "cumulative", "gauge"), "unit": units,
              "value": nullable_number, "missing_reason": enum(None, "unsupported", "permission_denied", "timeout", "source_lost", "reset", "collection_error", "no_data")})
histogram = obj({"kind": {"const": "histogram"}, "unit": {"const": "ms"},
                 "bounds_ms": {"const": bounds()}, "buckets": {"type": "array", "items": integer, "minItems": len(bounds()) + 1, "maxItems": len(bounds()) + 1},
                 "count": integer, "sum": number, "min": nullable_number, "max": nullable_number})
metrics = {"type": "object", "maxProperties": 1538, "propertyNames": code,
           "additionalProperties": {"oneOf": [scalar, histogram]}}
envelope = {"schema_version": {"const": 1}, "session_id": uuid, "source": enum("worker", "api", "runtime", "containers", "host", "postgres", "queue", "driver"),
            "instance_id": uuid, "sequence": integer, "start_utc": utc, "end_utc": utc,
            "duration_s": number, "elapsed_start_s": number, "elapsed_end_s": number,
            "quality": quality, "metrics": metrics}
settings = obj({"parallelism": {"type": "integer", "minimum": 1, "maximum": 64}, "batch_size": {"type": "integer", "minimum": 1},
                "lock_duration_s": number, "recovery_interval_s": number, "idle_delay_s": number,
                "error_delay_s": number, "http_timeout_s": number, "simulate_errors": {"type": "boolean"},
                "operator_mode": enum("http", "mock"), "environment": enum("test"),
                "interval_s": number, "process_sample_s": number, "queue_sample_s": number})
hash_value = {"type": "string", "pattern": "^[0-9a-f]{64}$"}
participant = obj({"instance_id": uuid, "role": enum("worker", "api", "driver", "postgres", "http"),
                   "worker_id": {"type": ["string", "null"], "maxLength": 256}, "boot_utc": utc})
inventory_value = obj({"value": nullable_number, "unit": enum("bytes", "cores"),
                       "status": enum("available", "unlimited", "unsupported")})
manifest = obj({"schema_version": {"const": 1}, "session_id": uuid, "profile": code,
                "commit": {"type": "string", "pattern": "^[0-9a-f]{40,64}$"}, "dirty": {"type": "boolean"},
                "source_sha256": hash_value, "settings_sha256": hash_value, "effective_settings": settings,
                "image_digests": {"type": "object", "propertyNames": code, "additionalProperties": {"type": "string", "pattern": "^sha256:[0-9a-f]{64}$"}},
                "tool_versions": {"type": "object", "propertyNames": code, "additionalProperties": code},
                "environment": obj({"backend": enum("wsl2", "hyperv", "unknown"), "os": {"const": "windows"},
                                    "resources": {"type": "object", "propertyNames": code, "additionalProperties": inventory_value}}),
                "campaign_id": uuid, "load": obj({"deliveries": integer, "message_bytes": integer, "email_bytes": integer, "replenishment": {"const": False}}),
                "planned_roles": {"type": "array", "items": enum("worker", "api", "driver", "postgres", "http")},
                "participants": {"type": "array", "items": participant}, "start_utc": utc,
                "stop_utc": {"anyOf": [utc, {"type": "null"}]}, "stop_reason": enum(None, "completed", "aborted", "stalled", "completion_stalled", "preflight_failed"),
                "workload_status": enum("preparing", "running", "completed", "aborted", "stalled", "completion_stalled"),
                "telemetry_status": enum("collecting", "complete", "partial", "failed")})
definition = obj({"unit": units, "kind": enum("delta", "cumulative", "gauge", "histogram"),
                  "description": {"type": "string", "maxLength": 2048}, "reset": enum("process_instance", "stats_reset", "none"),
                  "denominator": enum(None, "one_core", "allocated_capacity" )})
sample_context = obj({**{key: {"anyOf": [utc, {"type": "null"}]} for key in ("db_now", "server_start", "stats_reset", "wal_stats_reset", "io_stats_reset", "checkpointer_stats_reset")},
                      "campaign_id": uuid, "track_io_timing": enum("on", "off"), "track_wal_io_timing": enum("on", "off")},
                     optional=("db_now", "server_start", "stats_reset", "wal_stats_reset", "io_stats_reset", "checkpointer_stats_reset", "campaign_id", "track_io_timing", "track_wal_io_timing"))
schemas = {"manifest": manifest, "interval": obj(envelope),
           "sample": obj(dict(envelope, period_s=number, lag_s=number, receipt_utc=utc, process_instance_id=uuid, context=sample_context), optional=("process_instance_id", "context")),
           "lifecycle": obj(dict(envelope, event=enum("preparing", "ready", "processing", "draining", "completed", "aborted", "stalled", "completion_stalled", "process_started", "process_stopped", "source_lost", "collector_failure"))),
           "metric-definitions": obj({"schema_version": {"const": 1}, "histogram_bounds_ms": {"const": bounds()},
                                      "percentile_method": {"const": "nearest_rank"}, "definitions": {"type": "object", "minProperties": 1, "propertyNames": code, "additionalProperties": definition}})}
sid = "00000000-0000-4000-8000-000000000001"
iid = "00000000-0000-4000-8000-000000000002"
date = "2026-10-31T17:00:00Z"
base = dict(schema_version=1, session_id=sid, instance_id=iid, source="worker", sequence=0,
            start_utc=date, end_utc="2026-10-31T17:00:10Z", duration_s=10, elapsed_start_s=0,
            elapsed_end_s=10, quality="complete", metrics={"terminal.sent.delta": dict(kind="delta", unit="deliveries", value=3, missing_reason=None)})
examples = {"interval": base, "sample": dict(base, source="queue", period_s=30, lag_s=0, receipt_utc=date),
            "lifecycle": dict(base, source="driver", metrics={}, event="ready"),
            "metric-definitions": dict(schema_version=1, histogram_bounds_ms=bounds(), percentile_method="nearest_rank", definitions={
                "terminal.sent.delta": dict(unit="deliveries", kind="delta", description="Observed true terminal Sent persistence confirmations in this window; not mailbox delivery.", reset="process_instance", denominator=None)}),
            "manifest": dict(schema_version=1, session_id=sid, profile="small-http", commit="0" * 40, dirty=False,
                             source_sha256="0" * 64, settings_sha256="0" * 64, image_digests={}, tool_versions={"python": "3.11.15"},
                             effective_settings=dict(parallelism=4, batch_size=10, lock_duration_s=90, recovery_interval_s=15, idle_delay_s=.25,
                                                     error_delay_s=5, http_timeout_s=5, simulate_errors=False, operator_mode="http", environment="test",
                                                     interval_s=10, process_sample_s=5, queue_sample_s=30),
                             environment=dict(backend="unknown", os="windows", resources={}), campaign_id=sid,
                             load=dict(deliveries=3, message_bytes=64, email_bytes=32, replenishment=False), planned_roles=["worker"],
                             participants=[dict(instance_id=iid, role="worker", worker_id="synthetic-worker", boot_utc=date)],
                             start_utc=date, stop_utc=None, stop_reason=None, workload_status="preparing", telemetry_status="collecting")}

if __name__ == "__main__":
    for name, schema in schemas.items():
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema"
        for folder, suffix, data in [("schemas", ".v1.json", schema), ("examples", ".json", examples[name])]:
            target = ROOT / folder / (name + suffix)
            target.parent.mkdir(exist_ok=True)
            target.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
