from dataclasses import asdict, replace
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from baking_replay import Replay
from gen_instruction_catalog import read_catalog
from operand_specialization_data import Operand, training_handlers


class CrossGuestReplayTests(unittest.TestCase):
    """Run the same toy instruction stream through both native binding projections."""

    @classmethod
    def setUpClass(cls):
        cls.handlers = {h.name: i for i, h in training_handlers(read_catalog(), {}).items()}

    def models(self, cases, records):
        # a and b differ only in an immediate. u has no selected binding.
        base = 0x91000000  # ADD X0, X0, #imm
        arm = {"a": base | (4 << 10), "b": base | (8 << 10), "u": 0xd4000001}
        profiles = {cid: {"version": 2, "blocks": [
            {"words": [arm[c] for c in ops], "count": count} for count, ops in blocks
        ]} for cid, blocks in records.items()}
        a = arm["a"]
        arm_pools = {"entries": [{"word": a, "count": 1}], "partials": [base],
                     "pairs": [{"first": a, "second": a, "count": 1}],
                     "sequences": [{"words": [a] * 3, "count": 1}]}
        replay = Replay(tuple(arm_pools), arm_pools, cases, profiles, True)
        yield "aarch64", replay, (0, 1, 2, 3), .23

        # SHL byte ptr [ECX + disp8], imm8 uses a valid dynamic-immediate shape.
        op_id = self.handlers["OpGroup2_EbIb"]
        shape = Operand(0x61, 0, 7, 4, 5, 3, 4)
        other = replace(shape, imm=8)
        opcode = {"a": (op_id, shape), "b": (op_id, other), "u": (0, None)}
        exact = dict(originalId=op_id, width=1, kind="wrapper", operands=[asdict(shape)])
        x86_pools = {"exact": [exact], "dynamic-imm": [
            {**exact, "slots": 1, "operands": [asdict(shape) | {"imm": None}]}
        ], "fusion": [
            {"sequence": [op_id] * n, "operands": [asdict(shape)] * n, "width": n} for n in (2, 3)
        ]}

        def blocks(path):
            for count, ops in records[path.parent.name]:
                yield count, [opcode[c] for c in ops]

        with patch("collect_baked_fusions.blocks", blocks):
            replay = Replay(tuple(x86_pools), x86_pools, cases, Path("captures"), False)
        yield "i386", replay, (0, 1, 2, 3), .26

    def test_longest_dispatch_and_scalar_fallback_are_exclusive(self):
        records = {"one": [(9, "aaaa")], "two": [(1, "abab")]}
        for guest, model, (exact, broad, pair, triple), broad_credit in self.models(
                [{"id": "one"}, {"id": "two"}], records):
            with self.subTest(guest=guest):
                all_rows = {exact, broad, pair, triple}
                gains = model.by_family(all_rows)
                # Triple consumes positions 0..2. Position 3 takes exactly one scalar binding.
                self.assertAlmostEqual(gains["one"], (2 + .35) / 4)
                self.assertAlmostEqual(gains["two"], (.35 + broad_credit) / 2)
                self.assertAlmostEqual(model.by_family({exact, pair})["one"], 2 / 4)
                self.assertAlmostEqual(model.score(all_rows), (gains["one"] + gains["two"]) / 2)

    def test_fixed_and_unmatched_instructions_count_in_family_denominator(self):
        cases = [{"id": "pack", "family": "gzip"}, {"id": "unpack", "family": "gzip"},
                 {"id": "edit", "family": "editor"}]
        # A no-hit block contributes to pack's denominator. Nine executions of
        # unpack must not make it nine times as influential as pack.
        records = {"pack": [(1, "aaau"), (2, "u")], "unpack": [(9, "aa")], "edit": [(100, "u")]}
        for guest, model, (_, _, pair, triple), _ in self.models(cases, records):
            with self.subTest(guest=guest):
                selected = {pair, triple}
                gains = model.by_family(selected)
                self.assertAlmostEqual(gains["gzip"], (2 / 6 + 1 / 2) / 2)
                self.assertEqual(gains["editor"], 0)
                self.assertAlmostEqual(model.score(selected), 5 / 24)


if __name__ == "__main__":
    unittest.main()
