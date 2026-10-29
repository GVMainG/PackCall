import unittest
import uuid
from collectors import ScheduledCollector, CollectionError, parse_bytes, scalar
from contract import validate


class CollectorTests(unittest.TestCase):
    def test_units(self):
        self.assertEqual(1024**2, parse_bytes("1MiB"))
        self.assertEqual(1200, parse_bytes("1.2kB"))
        with self.assertRaises(CollectionError): parse_bytes("secret-canary")

    def test_slow_sample_records_cost_and_never_catches_up(self):
        now = [0.0]
        def sample():
            now[0] += 4
            raise CollectionError("timeout")
        collector = ScheduledCollector(str(uuid.uuid4()), "queue", 30, sample, clock=lambda: now[0])
        record = collector.tick()
        validate("sample", record)
        self.assertEqual("missing", record["quality"])
        self.assertEqual(4, record["duration_s"])
        self.assertEqual(34, collector.due)
        now[0] = 33
        self.assertIsNone(collector.tick())
        now[0] = 70
        late = collector.tick()
        self.assertEqual("missing", late["quality"])
        self.assertEqual(36, late["lag_s"])
        self.assertEqual(104, collector.due)

    def test_missing_is_not_zero(self):
        self.assertEqual("no_data", scalar(None)["missing_reason"])
        self.assertIsNone(scalar(0)["missing_reason"])

    def test_reset_token_change_is_explicit_without_resetting_database(self):
        now = [0.0]
        token = ["2026-10-31T10:00:00Z"]
        collector = ScheduledCollector(str(uuid.uuid4()), "postgres", 15,
            lambda: ({"postgres.xact_commit": scalar(5, kind="cumulative")}, {"stats_reset": token[0]}), clock=lambda: now[0])
        self.assertEqual("complete", collector.tick()["quality"])
        now[0] = 15
        token[0] = "2026-10-31T10:01:00Z"
        record = collector.tick()
        validate("sample", record)
        self.assertEqual("reset", record["quality"])


if __name__ == "__main__": unittest.main()
