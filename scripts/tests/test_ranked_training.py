import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import train_nested_presets as trainer
from nested_baking_workloads import file_digest, read_json, split_families, write_json


class RankedTrainingTests(unittest.TestCase):
    def test_shared_manifest_reserves_node_and_runs_each_case_once(self):
        manifest = trainer.REPO / "benchmark/optimization/manifests/baking-workloads.json"
        cases = read_json(manifest)
        training, held_out = split_families(cases, [])
        self.assertEqual({case["id"] for case in training},
                         {"gzip", "gunzip", "sed", "grep-fixed", "grep-regex", "sort", "sha256"})
        self.assertEqual([(case["id"], case["family"]) for case in held_out], [("node-minify", "node")])
        self.assertTrue(all(case["repetitions"] == 1 for case in cases))
        _, held_out = split_families(cases, ["gzip", "grep", "coreutils"])
        self.assertEqual({case["id"] for case in held_out}, {case["id"] for case in cases} - {"sed"})

    def test_default_cli_trains_each_guest_with_generalization_weight(self):
        cases = [{"id": "compress", "family": "gzip"}, {"id": "grep", "family": "grep"}]
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            config = root / "train.json"
            write_json(config, {"rootfs": {"aarch64": "/arm", "i386": "/x86"}, "workloads": cases})
            argv = ["train", "--config", str(config), "--output", str(root / "out"), "--holdout-family", "grep"]
            with (patch.object(sys, "argv", argv), patch.object(trainer, "trainer_fingerprint", return_value="source"),
                  patch.object(trainer, "train_i386") as i386, patch.object(trainer, "train_aarch64") as arm,
                  contextlib.redirect_stdout(io.StringIO())):
                trainer.main()
            for guest in (i386, arm):
                config, _, _, limit, _, _ = guest.call_args.args
                self.assertEqual(config["search"]["algorithm"], "joint-general")
                self.assertEqual(config["search"]["risk"], .5)
                self.assertEqual(config["search"]["depth"], 1)
                self.assertEqual(config["workloads"], cases[:1])
                self.assertEqual(limit, 6)

    def test_rank_only_and_linked_verification_share_one_ranking(self):
        words = [0x91000000 | (i << 10) for i in range(16)]
        pools = {"entries": [{"word": w} for w in words], "partials": [], "sequences": [], "pairs": []}
        profiles = {"train": {"version": 2, "blocks": [{"words": [w], "count": 1} for w in words]}}
        config = {"guest": "aarch64", "managed_cli": "mock", "workloads": [{"id": "train"}], "rank_only": True,
                  "search": {"algorithm": "joint-general", "risk": .5, "depth": 1, "min_family_ratio": 0.0}}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_json(root / "pools.json", pools)

            def build(cfg, build_dir, logs, jobs, plans=None):
                size = 100000
                if plans:
                    row = read_json(root / "rank-builds.json")[-1]
                    self.assertEqual((row["status"], row["name"]), ("reserved", plans.name))
                    size += len(read_json(plans / "scalar.json")["entries"]) * 400000
                build_dir.mkdir(exist_ok=True)
                lib = build_dir / "libtest"
                lib.write_text(str(size))
                return lib

            def measure(cfg, path, ref, rows, repeats):
                for trial in rows:
                    trial.update(score=1., worst_family=1.)

            with (patch.dict(trainer.PRIORS, entries=400000), patch.object(trainer, "build", side_effect=build),
                  patch.object(trainer, "text_size", side_effect=lambda cfg, lib: int(lib.read_text())),
                  patch.object(trainer, "stage_cli"), patch.object(trainer, "measure", side_effect=measure),
                  contextlib.redirect_stdout(io.StringIO())):
                trainer.ranked_presets(config, root, 1, 3, pools, {}, profiles, aarch64=True)
                self.assertFalse((root / "rank-builds.json").exists())
                rank_hash = file_digest(root / "ranking.json")
                config["rank_only"] = False
                with patch.object(trainer, "joint_rank", side_effect=AssertionError("reranked")):
                    trainer.ranked_presets(config, root, 1, 3, pools, {}, profiles, aarch64=True)
            archive = root / "frozen"
            manifest = read_json(archive / "manifest.json")
            self.assertEqual(file_digest(archive / "ranking.json"), rank_hash)
            self.assertEqual(manifest["training_provenance"]["budget"]["candidate_builds"], 3)
            previous = set()
            for tier in trainer.TIERS:
                row = manifest["presets"][f"{tier}m"]
                self.assertLess(previous, set(row["selection"]))
                previous = set(row["selection"])
                self.assertEqual(file_digest(Path(row["library"])), row["sha256"])
                self.assertLessEqual(row["text_delta_bytes"], tier * trainer.MIB)
