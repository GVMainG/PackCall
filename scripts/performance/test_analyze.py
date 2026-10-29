import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from analyze import analyze
from contract import ROOT,ContractError


class OfflineTests(unittest.TestCase):
    def test_repeatable_without_network_and_source_is_immutable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            manifest=json.loads((ROOT/"examples/manifest.json").read_text())
            record=json.loads((ROOT/"examples/interval.json").read_text())
            directory=root/"processes"/record["instance_id"];directory.mkdir(parents=True)
            files={root/"manifest.json":manifest,root/"initial.json":{"sent":0,"failed":0},root/"final.json":{"sent":3,"failed":0},
                   directory/"process.json":{"instance_id":record["instance_id"]},directory/"stopped.json":{"telemetry_status":"complete"}}
            for path,value in files.items():path.write_text(json.dumps(value))
            interval=directory/"intervals-000000.jsonl";interval.write_text(json.dumps(record)+"\n")
            before={path:path.read_bytes() for path in [*files,interval]}
            with patch("socket.create_connection",side_effect=AssertionError("network_forbidden")):
                first=analyze(root); first_bytes=(root/"summary.json").read_bytes()
                second=analyze(root)
            self.assertEqual(0,first["reconciliation_difference"])
            self.assertEqual(first,second)
            self.assertEqual(first_bytes,(root/"summary.json").read_bytes())
            self.assertTrue((root/"charts/volume.svg").exists())
            for path,original in before.items():self.assertEqual(original,path.read_bytes())
            record["schema_version"]=2;interval.write_text(json.dumps(record)+"\n")
            with self.assertRaises(ContractError):analyze(root)


if __name__=="__main__":unittest.main()
