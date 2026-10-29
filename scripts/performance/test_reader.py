import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from contract import ROOT, ContractError, read
from reader import read_prefix


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "intervals-000000.jsonl"
        self.record = read("interval", ROOT / "examples/interval.json")

    def write(self, records, tail=b""):
        self.path.write_bytes(b"".join(json.dumps(row).encode() + b"\n" for row in records) + tail)

    def test_truncated_tail_preserves_prefix_bytes(self):
        self.write([self.record], b'{"secret-canary')
        before = self.path.read_bytes()
        result = read_prefix([self.path])
        self.assertEqual(1, len(result["records"]))
        self.assertIn("truncated_tail", result["quality"])
        self.assertEqual(before, self.path.read_bytes())

    def test_middle_corruption_rejected(self):
        self.write([self.record], b'{broken}\n{}\n')
        with self.assertRaises(ContractError): read_prefix([self.path])

    def test_duplicate_conflict_gap_and_reset(self):
        self.write([self.record, self.record])
        self.assertEqual(1, len(read_prefix([self.path])["records"]))
        second = copy.deepcopy(self.record)
        second["metrics"]["terminal.sent.delta"]["value"] += 1
        self.write([self.record, second])
        with self.assertRaises(ContractError): read_prefix([self.path])
        second.update(sequence=2, elapsed_start_s=20, elapsed_end_s=30)
        self.write([self.record, second])
        self.assertIn("gap", read_prefix([self.path])["quality"])

    def test_checksum_is_for_closed_file_only(self):
        self.write([self.record])
        checksum = hashlib.sha256(self.path.read_bytes()).hexdigest()
        (self.path.parent / "checksums.json").write_text(json.dumps({self.path.name: checksum}))
        self.assertEqual(1, len(read_prefix([self.path])["records"]))
        with self.path.open("ab") as stream: stream.write(b" ")
        with self.assertRaises(ContractError): read_prefix([self.path])


if __name__ == "__main__": unittest.main()
