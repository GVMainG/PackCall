import copy
import unittest
from contract import ContractError, ROOT, read, validate, bounds


class ContractTests(unittest.TestCase):
    def test_examples(self):
        for kind in ("manifest", "interval", "sample", "lifecycle", "metric-definitions"):
            with self.subTest(kind=kind):
                read(kind, ROOT / "examples" / f"{kind}.json")

    def test_every_required_field_and_schema_version(self):
        for kind in ("manifest", "interval", "sample", "lifecycle", "metric-definitions"):
            good = read(kind, ROOT / "examples" / f"{kind}.json")
            for key in good:
                bad = copy.deepcopy(good)
                del bad[key]
                with self.subTest(kind=kind, missing=key), self.assertRaises(ContractError):
                    validate(kind, bad)
            good["schema_version"] = 2
            with self.assertRaises(ContractError):
                validate(kind, good)

    def test_units_and_safe_errors(self):
        item = read("interval", ROOT / "examples/interval.json")
        for unit in ("seconds", "ms", "secret-payload-canary"):
            item["metrics"]["terminal.sent.delta"]["unit"] = unit
            with self.assertRaises(ContractError) as error:
                validate("interval", item)
            self.assertNotIn(unit, str(error.exception))

    def test_histogram_relationships(self):
        item = read("interval", ROOT / "examples/interval.json")
        histogram = dict(kind="histogram", unit="ms", bounds_ms=bounds(), buckets=[0] * (len(bounds()) + 1), count=0, sum=0, min=None, max=None)
        item["metrics"] = {"operator.call": histogram}
        validate("interval", item)
        histogram["buckets"][0] = 1
        with self.assertRaises(ContractError):
            validate("interval", item)
        histogram.update(count=1, min=0, max=0)
        validate("interval", item)
        histogram["unit"] = "s"
        with self.assertRaises(ContractError):
            validate("interval", item)

    def test_unknown_fields_missing_and_monotonic_bounds(self):
        item = read("interval", ROOT / "examples/interval.json")
        item["email"] = "secret-payload-canary"
        with self.assertRaises(ContractError):
            validate("interval", item)
        del item["email"]
        item["elapsed_end_s"] = 9
        with self.assertRaises(ContractError):
            validate("interval", item)
        item["elapsed_end_s"] = 10
        item["metrics"]["terminal.sent.delta"]["value"] = None
        with self.assertRaises(ContractError):
            validate("interval", item)


if __name__ == "__main__":
    unittest.main()
