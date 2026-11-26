import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from gen_operand_specializations import generate_aarch64
from train_nested_presets import (
    ARM_CLASSES, arm_partial_pool, arm_plan, guest_configs, load_training_config, rank_arm_profiles,
)


class SharedWorkloadTests(unittest.TestCase):
    def test_both_guests_share_workload_entries_and_select_their_rootfs(self):
        workloads = [{"id": "sort", "sha256": "expected"}]
        config = {"rootfs": {"i386": "/guest32", "aarch64": "/guest64"}, "workloads": workloads}
        i386, aarch64 = guest_configs(config)
        self.assertEqual((i386["guest"], i386["rootfs"]), ("i386", "/guest32"))
        self.assertEqual((aarch64["guest"], aarch64["rootfs"]), ("aarch64", "/guest64"))
        self.assertIs(i386["workloads"], workloads)
        self.assertIs(aarch64["workloads"], workloads)
        self.assertEqual(guest_configs(config, "aarch64"), [aarch64])

    def test_legacy_config_uses_its_guest_and_rootfs(self):
        for guest in ("i386", "aarch64"):
            config = {"rootfs": "/guest", "guest": guest}
            self.assertEqual(guest_configs(config), [config])
        with self.assertRaises(ValueError):
            guest_configs(config, "both")

    def test_shared_config_requires_both_rootfs_paths(self):
        with self.assertRaises(ValueError):
            guest_configs({"rootfs": {"aarch64": "/guest64"}})

    def test_external_workloads_are_resolved_and_fingerprinted(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "training.json"
            manifest = Path(directory) / "workloads.json"
            config.write_text('{"workloads": "workloads.json"}')
            manifest.write_text('[{"id": "sort"}]')
            values, old_hash = load_training_config(config)
            self.assertEqual(values["workloads"], [{"id": "sort"}])
            manifest.write_text('[{"id": "sed"}]')
            self.assertNotEqual(load_training_config(config)[1], old_hash)


class ShortSequenceTests(unittest.TestCase):
    def test_shared_interior_is_offered_even_when_maximal_runs_differ(self):
        profiles = [dict(version=2, sequences=[dict(words=[x, 2, 3, 4, x], count=3)])
                    for x in (1, 5)]
        rows = rank_arm_profiles(profiles, ["gzip", "sed"])["sequences"]
        weights = {row["words"]: row["count"] for row in rows}
        self.assertEqual(weights[2, 3, 4], 2 * weights[1, 2, 3, 4, 1])
        duplicate = rank_arm_profiles([profiles[0], *profiles], ["gzip", "gzip", "sed"])
        self.assertEqual(duplicate["sequences"], rows)


class Imm12TrainingTests(unittest.TestCase):
    def test_partial_shapes_survive_exact_budget_cut(self):
        add, load = 0x91000129, 0xf9400020
        profiles = [{"entries": [
            {"word": add + (1 << 10), "count": 100},
            {"word": add + (3 << 10), "count": 2},
            {"word": load + (2 << 10), "count": 8},
            {"word": load + (4 << 10), "count": 7},
        ]}]
        pools = rank_arm_profiles(profiles)
        pools["partials"] = arm_partial_pool(pools["entries"], 1, 32)
        plan = arm_plan(pools, (1, 32, 0, 0))
        self.assertEqual([row["word"] for row in plan["entries"]], [add + (1 << 10)])
        self.assertEqual(plan["partials"], [load, add])
        generated = generate_aarch64(plan, 1, 0, 0, partial_budget=1)
        self.assertIn(f"BakedBinding<0x{load:08x}u, true>", generated)
        self.assertNotIn(f"BakedBinding<0x{add:08x}u, true>", generated)
        self.assertEqual(arm_partial_pool(pools["entries"], 1, 0), [])

    def test_explicit_shapes_require_canonical_unique_encodings(self):
        for shapes in ([0x91000400], [0x14000000], [0xf9400000, 0xf9400000]):
            with self.subTest(shapes=shapes), self.assertRaises(ValueError):
                generate_aarch64({"version": 1, "architecture": "aarch64", "entries": [],
                                  "partials": shapes}, 0, 0, 0)


class GrowthBudgetTests(unittest.TestCase):
    def test_arm_tiers_keep_all_smaller_plan_prefixes(self):
        pools = {name: list(range(4)) for name in ARM_CLASSES}
        small = arm_plan(pools, (1, 0, 2, 1))
        large = arm_plan(pools, (3, 2, 2, 4))
        for name in ARM_CLASSES:
            self.assertEqual(large[name][:len(small[name])], small[name])
