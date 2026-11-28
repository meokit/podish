import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from aarch64_baked_words import canonicalize_baked_word
from aarch64_baking import partial_pairs, partial_shape
from baking_rank_cost import arm_symbol, binding
from baking_replay import arm_layouts
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


    def test_32_word_runs_supply_inner_windows_within_the_fusion_limit(self):
        seq = list(range(32))
        profile = dict(version=2, sequences=[dict(words=seq, count=3)])
        rows = rank_arm_profiles([profile])["sequences"]
        self.assertIn(tuple(seq[-4:]), [row["words"] for row in rows])
        self.assertTrue(all(len(row["words"]) <= 24 for row in rows))


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
        self.assertIn(f"ScalarHandlers<0x{load:08x}u, true>", generated)
        self.assertNotIn(f"ScalarHandlers<0x{add:08x}u, true>", generated)
        self.assertEqual(arm_partial_pool(pools["entries"], 1, 0), [])

    def test_explicit_shapes_require_canonical_unique_encodings(self):
        for shapes in ([0x91000400], [0x14000000], [0xf9400000, 0xf9400000]):
            with self.subTest(shapes=shapes), self.assertRaises(ValueError):
                generate_aarch64({"version": 1, "architecture": "aarch64", "entries": [],
                                  "partials": shapes}, 0, 0, 0)


class PartialImmediateTests(unittest.TestCase):
    def test_partial_keys_keep_width_registers_addressing_mode_and_shift(self):
        for word, key in (
            (0xa9bf0460, 0xa9800460),  # stp x0, x1, [x3, #-16]!
            (0xf85f8460, 0xf8400460),  # ldr x0, [x3], #-8
            (0xf2b579a0, 0xf2a00000),  # movk x0, #0xabcd, lsl #16
            (0x92401c62, 0x92000062),  # and x2, x3, #0xff
        ):
            with self.subTest(word=hex(word)):
                self.assertEqual(partial_shape(word), key)
                self.assertEqual(partial_shape(key), key)
        self.assertIsNone(partial_shape(0x9240fc62))  # reserved bitmask
        self.assertIsNone(partial_shape(0xf85f8060))  # unscaled offset, no writeback

    def test_partial_pairs_require_distinct_encodings_and_allow_transfer_starts(self):
        ldr, cbz = 0xf9400020, 0xb4000000
        pairs = [(ldr + (1 << 10), cbz + (2 << 5)),
                 (ldr + (3 << 10), cbz + (5 << 5))]
        profile = dict(version=2, entries=[dict(word=w, count=1) for row in pairs for w in row],
                       blocks=[dict(words=row, count=3) for row in pairs])
        ranked = partial_pairs([profile], ["test"], 9)
        self.assertEqual(len(ranked), 1)
        self.assertEqual(ranked[0], dict(first=ldr, second=cbz, dynamic_mask=1, count=100_000_000))

    def test_pair_plan_distinguishes_dynamic_members_and_keeps_exact_rows(self):
        adr, add = 0x90000000, 0x91000000
        partial = dict(first=adr, second=add, dynamic_mask=2, count=2)
        exact = dict(first=adr, second=add, count=1)
        plan = dict(version=1, architecture="aarch64", entries=[], pairs=[exact, partial])
        generated = generate_aarch64(plan, 0, 2, 0)
        self.assertIn("BakedFusion<0x90000000u, 0x91000000u>", generated)
        self.assertIn("PartialBakedFusion<2, 0x90000000u, 0x91000000u>", generated)
        for bad in (dict(partial, second=add + (1 << 10)), dict(partial, dynamic_mask=4),
                    dict(partial, first=0x14000000), dict(partial, dynamic_mask=True)):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                generate_aarch64(dict(plan, pairs=[bad]), 0, 2, 0)


class BakedLookupTests(unittest.TestCase):
    def test_tables_keep_priority_cuts_and_longest_prefix_order(self):
        add = 0x91000400
        plan = {
            "architecture": "aarch64", "version": 1,
            "entries": [{"word": add + i, "count": 1} for i in [4, 2, 3]],
            "pairs": [{"first": add + i, "second": add, "count": 1} for i in [3, 1, 2]],
            "sequences": [
                {"words": [add, add, add + 1], "count": 1},
                {"words": [add, add, add, add], "count": 1},
                {"words": [add, add, add], "count": 1},
                {"words": [add + 1, add, add], "count": 1},
            ],
        }
        generated = generate_aarch64(plan, 2, 2, 4)
        self.assertIn("std::array<ScalarBinding, 2> BakedExact", generated)
        self.assertLess(generated.index("{0x91000402u,"), generated.index("{0x91000403u,"))
        self.assertNotIn("{0x91000404u,", generated)
        self.assertIn("std::array<PairBinding, 2> BakedPairs", generated)
        self.assertLess(generated.index("0x9100040191000400ull"), generated.index("0x9100040291000400ull"))
        sequence_rows = generated.split(" BakedSequences = ", 1)[1].splitlines()[1:5]
        self.assertEqual([row.split("aarch64::", 1)[0].strip() for row in sequence_rows],
                         ["{0x91000400u, 0, 4,", "{0x91000400u, 3, 3,",
                          "{0x91000400u, 5, 3,", "{0x91000401u, 7, 3,"])
        self.assertIn("0x91000400u, 0x91000400u, 0x91000401u>", sequence_rows[2])

    def test_pc_relative_keys_preserve_instruction_fields(self):
        for word, key in (
            (0x17ffffff, 0x14000000), (0x97ffffff, 0x94000000),
            (0x54ffffed, 0x5400000d), (0x54fffffd, 0x54fffffd),
            (0xb5ffffe9, 0xb5000009), (0xb7ffffe9, 0xb7f80009),
            (0x70ffffea, 0x1000000a), (0xf0ffffea, 0x9000000a),
            (0x9cffffeb, 0x9c00000b), (0xd8ffffec, 0xd800000c),
            (0xdcffffeb, 0xdcffffeb), (0x91000400, 0x91000400),
        ):
            with self.subTest(word=hex(word)):
                self.assertEqual(canonicalize_baked_word(word), key)
                self.assertEqual(canonicalize_baked_word(key), key)

    def test_pc_relative_counts_pool_before_generator_and_trainer_rank(self):
        add, branch = 0x91000400, 0x54000001
        variants = [(6, branch + 0x40), (6, branch + 0xa0), (10, branch + 1)]
        plan = {
            "architecture": "aarch64", "version": 2,
            "entries": [dict(word=w, count=n) for n, w in variants],
            "pairs": [dict(first=add, second=w, count=n) for n, w in variants],
            "sequences": [dict(words=[add, add, w], count=n) for n, w in variants],
            "blocks": [{"words": [add, add, branch + 0x140], "count": 7}],
        }
        generated = generate_aarch64(plan, 1, 1, 1)
        self.assertIn("std::array<ScalarBinding, 1> BakedExact", generated)
        self.assertIn("ScalarHandlers<0x54000001u>", generated)
        self.assertNotIn("ScalarHandlers<0x54000002u>", generated)
        self.assertIn("0x9100040054000001ull", generated)
        self.assertIn("BakedFusion<0x91000400u, 0x91000400u, 0x54000001u>", generated)

        pools = rank_arm_profiles([plan]) | {"partials": []}
        self.assertEqual([row["word"] for row in pools["entries"]], [branch, branch + 1])
        self.assertEqual([row["second"] for row in pools["pairs"]], [branch, branch + 1])
        self.assertEqual([row["words"][-1] for row in pools["sequences"]], [branch, branch + 1])
        ids = dict(entries=[0, 1], partials=[], pairs=[2, 3], sequences=[4, 5])
        self.assertEqual(list(arm_layouts(pools, ids, [{"id": "test"}], {"test": plan})),
                         [("test", 7, ((4,), (2,), (0,)))])

        address = 0xf0ffffea  # ADRP X10, -1 page
        symbol = f"15DispatchWrapperIXadL_ZN7fiberish7aarch646detail10BakedLogicILj0ELb0EJLj{address}EEEE"
        key, feature = binding("entries", {"word": address})
        self.assertEqual((key, feature), ("entries/9000000a", ("entries", 1, 0x90)))
        self.assertEqual(arm_symbol(symbol), key)


class GrowthBudgetTests(unittest.TestCase):
    def test_arm_tiers_keep_all_smaller_plan_prefixes(self):
        pools = {name: list(range(4)) for name in ARM_CLASSES}
        small = arm_plan(pools, (1, 0, 2, 1))
        large = arm_plan(pools, (3, 2, 2, 4))
        for name in ARM_CLASSES:
            self.assertEqual(large[name][:len(small[name])], small[name])
