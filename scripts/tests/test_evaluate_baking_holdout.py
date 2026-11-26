import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import evaluate_baking_holdout as evaluation
from nested_baking_workloads import file_digest, read_json, write_json


class FrozenHoldoutTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.config = self.root / "config.json"
        self.frozen = self.root / "frozen"
        self.output = self.root / "evaluation"
        self.train = [{"id": "gzip", "family": "gzip"}, {"id": "gunzip", "family": "gzip"}]
        self.holdout = [{"id": "sed"}, {"id": "grep-fixed", "family": "grep"}]
        write_json(self.config, {"rootfs": {"i386": "/32", "aarch64": "/64"}, "managed_cli": "/managed",
                                 "workloads": self.train + self.holdout})
        self.search = {"guest": "aarch64", "train_cases": self.train, "holdout_cases": self.holdout}
        self.provenance = {"config_sha256": file_digest(self.config), "profiles": {"gzip": "1", "gunzip": "2"},
                           "search": self.search}
        presets = {}
        for name in ("baseline", "1.5m", "3m", "10m"):
            directory = self.frozen / name
            directory.mkdir(parents=True)
            library = directory / "libfibercpu.dylib"
            library.write_text(name)
            presets[name] = dict(library=f"/before-relocation/{name}/{library.name}",
                                 sha256=file_digest(library), text_bytes=123, score=999)
        self.manifest = {"training_provenance": self.provenance, "presets": presets}
        self.stage = self.enterContext(patch.object(evaluation, "stage_cli", return_value=self.root / "staged"))
        self.measure = self.enterContext(patch.object(evaluation, "measure", side_effect=self.measure_ok))

    def evaluate(self, **kwargs):
        write_json(self.frozen / "manifest.json", self.manifest)
        evaluation.evaluate(self.config, self.frozen, self.output, **kwargs)

    def measure_ok(self, cfg, directory, reference_cli, trials, repeats):
        self.assertEqual(cfg["rootfs"], "/64")
        self.assertEqual(cfg["workloads"], self.holdout)
        self.assertEqual([t["name"] for t in trials], ["1.5m", "3m", "10m"])
        for trial in trials:
            self.assertNotIn("score", trial)
            trial.update(score=1.1, worst_family=1.0, cases={"sed": 1.0, "grep-fixed": 1.21},
                         families={"sed": 1.0, "grep": 1.21},
                         samples={"sed": [1.0] * repeats, "grep-fixed": [1.21] * repeats})

    def test_frozen_default_reference_all_tiers_and_no_training_case_runs(self):
        self.evaluate()
        report = read_json(self.output / "report.json")
        self.assertEqual(report["status"], "complete")
        self.assertEqual(report["reference"]["sha256"], self.manifest["presets"]["baseline"]["sha256"])
        self.assertEqual(report["manifest_sha256"], file_digest(self.frozen / "manifest.json"))
        self.assertEqual(list(report["results"]), ["1.5m", "3m", "10m"])
        self.assertEqual(report["results"]["10m"]["score"], 1.1)
        self.assertEqual(report["results"]["3m"]["families"], {"sed": 1.0, "grep": 1.21})

    def test_external_reference_is_staged_and_identified(self):
        other = self.root / "production"
        other.write_text("reference")
        self.evaluate(reference_library=other, repeats=1)
        self.assertEqual(self.stage.call_args.args[-1], str(other.resolve()))
        self.assertEqual(read_json(self.output / "report.json")["reference"]["sha256"], file_digest(other))

    def test_rejects_same_family_across_boundary(self):
        self.search["train_cases"] = self.train[:1] + self.holdout[1:]
        self.search["holdout_cases"] = self.train[1:] + self.holdout[:1]
        with self.assertRaisesRegex(ValueError, "split"):
            self.evaluate()
        self.measure.assert_not_called()

    def test_rejects_capture_from_unseen_case(self):
        self.provenance["profiles"]["sed"] = "extra-profile"
        with self.assertRaisesRegex(ValueError, "profiles"):
            self.evaluate()
        self.measure.assert_not_called()

    def test_rejects_modified_artifacts_and_configuration(self):
        (self.frozen / "3m/libfibercpu.dylib").write_text("different")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.evaluate()
        cfg = read_json(self.config)
        cfg["workloads"][0]["shell"] = "gzip -9"
        write_json(self.config, cfg)
        with self.assertRaisesRegex(ValueError, "configuration"):
            self.evaluate()
        self.measure.assert_not_called()

    def test_incomplete_repetition_invalidates_entire_report(self):
        def incomplete(*args):
            self.measure_ok(*args)
            args[3][-1]["samples"]["sed"].pop()

        self.measure.side_effect = incomplete
        with self.assertRaisesRegex(ValueError, "did not complete"):
            self.evaluate()
        report = read_json(self.output / "report.json")
        self.assertEqual(report["status"], "failed")
        self.assertNotIn("results", report)

    def test_rejects_archive_without_prior_holdout(self):
        self.search["holdout_cases"] = []
        with self.assertRaisesRegex(ValueError, "held out"):
            self.evaluate()
        self.measure.assert_not_called()
