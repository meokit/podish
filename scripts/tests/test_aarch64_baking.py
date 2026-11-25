import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from gen_operand_specializations import generate_aarch64
from train_nested_presets import aarch64_plan


class Imm12TrainingTests(unittest.TestCase):
    def test_partial_shapes_survive_exact_budget_cut(self):
        add, load = 0x91000129, 0xf9400020
        profiles = [{"entries": [
            {"word": add + (1 << 10), "count": 100},
            {"word": add + (3 << 10), "count": 2},
            {"word": load + (2 << 10), "count": 8},
            {"word": load + (4 << 10), "count": 7},
        ]}]
        plan = aarch64_plan(profiles, budget=1)
        self.assertEqual([row["word"] for row in plan["entries"]], [add + (1 << 10)])
        self.assertEqual(plan["partials"], [load, add])
        generated = generate_aarch64(plan, 1, 0, 0, partial_budget=1)
        self.assertIn(f"BakedBinding<0x{load:08x}u, true>", generated)
        self.assertNotIn(f"BakedBinding<0x{add:08x}u, true>", generated)
        self.assertEqual(aarch64_plan(profiles, 1, partial_budget=0)["partials"], [])

    def test_explicit_shapes_require_canonical_unique_encodings(self):
        for shapes in ([0x91000400], [0x14000000], [0xf9400000, 0xf9400000]):
            with self.subTest(shapes=shapes), self.assertRaises(ValueError):
                generate_aarch64({"version": 1, "architecture": "aarch64", "entries": [],
                                  "partials": shapes}, 0, 0, 0)
