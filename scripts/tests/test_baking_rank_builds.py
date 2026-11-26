import hashlib
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from baking_rank_builds import materialize
from nested_baking_workloads import read_json, write_json


class RankedBuilds(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.ranking = dict(entries=[dict(id=i, group_end=i != 1, cumulative_bytes=10*(i+1)) for i in range(5)],
                            cuts=[3, 4, 5])
        self.tiers = [x / (1 << 20) for x in (30, 40, 50)]
        self.compiled = []

    def compile(self, row, ids):
        self.compiled.append(len(ids))
        data = bytes(ids)
        lib = self.root / row["name"] / "library"
        lib.parent.mkdir()
        lib.write_bytes(data)
        row.update(library=str(lib), text_delta_bytes=len(ids)*10, library_sha256=hashlib.sha256(data).hexdigest())
        write_json(lib.parent / "build.json", row)

    def build(self, budget=3, compiler=None):
        return materialize(self.root, self.ranking, self.tiers, budget, compiler or self.compile)[0]

    def test_link_once_per_distinct_prefix_and_check_digest_on_resume(self):
        self.ranking["cuts"] = [3, 3, 5]
        self.assertEqual([r["cut"] for r in self.build()], [3, 3, 5])
        self.build()
        self.assertEqual(self.compiled, [3, 5])
        (self.root / read_json(self.root / "rank-builds.json")[0]["name"] / "library").write_bytes(b"wrong")
        with self.assertRaisesRegex(ValueError, "changed"):
            self.build()

    def test_oversize_reuses_original_at_later_tier(self):
        self.ranking["cuts"] = [3, 3, 5]
        self.tiers[0] = 29 / (1 << 20)
        with self.assertRaisesRegex(RuntimeError, "increase --max-builds"):
            self.build(budget=1)
        self.assertEqual([r["cut"] for r in self.build()], [1, 3, 5])
        self.assertEqual(self.compiled, [3, 1, 5])

    def test_durable_archive_recovers_reserved_attempt(self):
        def crash(row, ids):
            self.compile(row, ids)
            raise InterruptedError
        with self.assertRaises(InterruptedError):
            self.build(compiler=crash)
        self.build()
        self.assertEqual(self.compiled, [3, 4, 5])
        self.ranking["entries"][0]["id"] = 9
        with self.assertRaisesRegex(ValueError, "different candidate order"):
            self.build()

    def test_no_partial_dependency_build(self):
        self.ranking["cuts"][0] = 2
        with self.assertRaisesRegex(ValueError, "dependency group"):
            self.build()
        self.assertFalse(self.compiled)
