from dataclasses import asdict, replace
from pathlib import Path
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from baking_replay_i386 import layouts
from gen_instruction_catalog import read_catalog
from operand_specialization_data import Operand, training_handlers


def write_capture(path, records):
    path.parent.mkdir()
    with path.open("wb") as stream:
        stream.write(struct.pack("<IIQiiI", 0x324b4c42, 3, 0, len(records), 0, 0))
        for count, ops in records:
            stream.write(struct.pack("<IIIQ", 0, 0, len(ops), count))
            for op_id, op in ops:
                stream.write(struct.pack("<QIBBBBIIQi", op.ea_desc << 32 | op.disp, 0,
                                         op.length, op.modrm, op.prefixes, op.meta, op.imm, op_id, 0, 0))


class I386LayoutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.catalog = read_catalog()
        cls.handlers = {h.name: i for i, h in training_handlers(cls.catalog, {}).items()}

    def replay(self, pools, ids, *records):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            write_capture(path / "test" / "blocks.bin", records)
            return list(layouts(pools, ids, [{"id": "test"}], path))

    def test_partial_priority_slot_width_policy_and_modrm(self):
        op_id = self.handlers["OpGroup2_EvIb"]
        op = Operand(0x61, 0x40, 0x17, 5, 5, 120, 129)
        scalar = dict(originalId=op_id, operands=[asdict(op)])
        base = dict(training_id=op_id, slots=2, modrm=op.modrm)

        def partial(**fields):
            return {**scalar, "slots": 2, "operands": [asdict(op) | fields]}

        pools = dict(exact=[scalar], policies=[base | dict(width=16, flags="dead")], modrm=[base])
        pools.update({"dynamic-disp": [partial(disp=None)], "dynamic-imm": [partial(imm=None)],
                      "dynamic-both": [partial(disp=None, imm=None)]})
        ids = {"exact": [0], "dynamic-disp": [1], "dynamic-imm": [2], "dynamic-both": [3],
               "policies": [4], "modrm": [5]}
        ops = [(op_id, op), (op_id, replace(op, disp=111)), (op_id, replace(op, imm=134)),
               (op_id, replace(op, imm=134, disp=111)), (op_id, replace(op, imm=1))]
        self.assertEqual(self.replay(pools, ids, (19, ops)), [
            ("test", 19, ((0, 1, 2, 3, 4, 5), (1, 3, 4, 5), (2, 3, 4, 5), (3, 4, 5), ())),
        ])

    def test_modrm_memory_mask_register_bits_and_missing_shapes(self):
        op_id = self.handlers["OpAdd_EvGv"]
        mem = Operand(0x4e, 0, 3, 3, 5, 0, 0)
        reg = Operand(0xce, 0, 1, 2, 0, 0, 0)
        pools = dict(modrm=[dict(training_id=op_id, slots=1, modrm=8),
                            dict(training_id=op_id, slots=1, modrm=0xce)])
        ops = [(op_id, mem), (op_id, reg), (0, reg)]
        self.assertEqual(self.replay(pools, {"modrm": [0, 1]}, (7, ops)), [("test", 7, ((0,), (1,), ()))])

    def test_longest_fusion_first_branch_ends_window_and_ret_imm_remains_fixed(self):
        alu = self.handlers["OpAdd_EvGv"]
        branch = self.handlers["OpJcc_E_Rel8"]
        ret = self.handlers[self.catalog["fallbacks"]["194"]]
        reg = Operand(0xc1, 0, 1, 2, 0, 0, 0)
        control = Operand(0, 0, 0x8c, 2, 0, 0, 51)
        normalized = replace(control, imm=0)
        ret_op = replace(control, length=3, imm=8)

        def fusion(*ops):
            return dict(sequence=[i for i, _ in ops], operands=[asdict(o) for _, o in ops])

        a, b = (alu, reg), (branch, normalized)
        pools = dict(fusion=[fusion(a, a), fusion(a, a, b), fusion(a, b), fusion(b, a)],
                     exact=[dict(originalId=branch, operands=[asdict(normalized)]),
                            dict(originalId=ret, operands=[asdict(ret_op)])])
        blocks = [(a, a, (branch, control), a), ((ret, replace(ret_op, imm=16)), (ret, ret_op))]
        self.assertEqual(self.replay(pools, {"fusion": [0, 1, 2, 3], "exact": [4, 5]},
                                     (5, blocks[0]), (1, blocks[1])), [
            ("test", 5, ((1, 0), (2,), (4,), ())), ("test", 1, ((), (5,))),
        ])


if __name__ == "__main__":
    unittest.main()
