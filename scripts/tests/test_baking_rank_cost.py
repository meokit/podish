import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import baking_rank_cost as cost


class StaticCostTests(unittest.TestCase):
    def test_arm_dispatch_wrappers_map_to_logical_keys(self):
        self.assertEqual(
            cost.arm_symbol("15DispatchWrapperIXadL_ZN7fiberish7aarch646detail10BakedLogicILj1ELb1EJLj13EEEE"),
            cost.binding("partials", 13)[0],
        )
        self.assertEqual(
            cost.arm_symbol("15DispatchWrapperIXadL_ZN7fiberish7aarch646detail10BakedLogicILj1ELb0EJLj5ELj7ELj11EEEE"),
            cost.binding("sequences", {"words": [5, 7, 11]})[0],
        )
        for dead in (0, 1, 2):
            self.assertEqual(
                cost.arm_symbol(f"15DispatchWrapperIXadL_ZN7fiberish7aarch646detail10BakedLogicILj{dead}ELb0EJLj5ELj7EEEE"),
                cost.binding("pairs", {"first": 5, "second": 7})[0],
            )
        self.assertIsNone(cost.arm_symbol("12BakedExact"))
        self.assertIsNone(cost.arm_symbol("RunBlock"))
        self.assertIsNone(cost.arm_symbol("10BakedLogicILj1ELb1EJLj13EEE"))

    def test_x86_operand_abi_and_missing_fields(self):
        name = "OpMov_GvEv"
        idx = cost.scalar_name_ids()[name]
        operand = dict(zip(cost.OPERAND, (0, 0, 3, 2, 526336, 0, 0)))
        prefix = "15DispatchWrapperIXadL_ZNS_13baked_operand10OpMov_GvEvINS_11OperandSpec"
        shape = "IXtlNS_19DecodedOperandShapeELj33751040ELj526336EEELj2E"
        key = cost.binding("exact", {"originalId": idx, "operands": [operand]})[0]
        self.assertEqual(cost.x86_symbol(prefix + "7Profile" + shape + "EEE", {}), key)
        key = cost.binding("dynamic-both", {"originalId": idx, "slots": 2,
                                           "operands": [dict(operand, disp=None, imm=None)]})[0]
        self.assertEqual(cost.x86_symbol(prefix + "14PartialProfile" + shape + "Lj2ELj3EEE", {}), key)

    def test_clone_ordinal_is_local_and_recipe_name_must_match(self):
        symbol = "12OperandClone_002_SuperOpcode_2_000_A__BEPNS_8EmuState"
        self.assertEqual(cost.x86_symbol(symbol, {(2, "SuperOpcode_2_000_A__B"): "recipe-one"}), "recipe-one")
        self.assertEqual(cost.x86_symbol(symbol, {(2, "SuperOpcode_2_000_A__B"): "recipe-two"}), "recipe-two")
        self.assertIsNone(cost.x86_symbol(symbol, {(2, "SuperOpcode_2_000_C__D"): "recipe-one"}))

    def test_target_mismatch_fails_before_pricing(self):
        cache = dict(version=cost.VERSION, guest="i386", toolchain={"flags": "old"})
        with patch.object(cost, "read", return_value=cache), patch.object(cost, "fingerprint", return_value={}):
            with self.assertRaisesRegex(ValueError, "cost cache baseline"):
                cost.rank_costs("unused", "i386", [], {}, ".")

    def test_single_run_cross_validates_on_distinct_plans(self):
        trials = [cost.Trial("production", "plan-"+k, (k,), k, {k: 15}, 30) for k in ("1", "2", "2")]
        features = {k: ("entries", 1, 1) for k in "12"}
        with patch.object(cost, "collect", return_value=(trials, features, {})), patch.object(
                cost, "fit", wraps=cost.fit) as fit:
            cache = cost.compile_cache(Path("."), "aarch64")
        self.assertEqual([len(call.args[0]) for call in fit.call_args_list], [1, 1, 2])
        self.assertEqual(cache["validation"]["distinct"], 2)
        self.assertEqual(cache["validation"]["method"], "leave one distinct table out")

    def test_backtest_purges_whole_tables_and_their_symbols(self):
        trials = []
        for i, method in enumerate(("first", "second", "third", "fourth")):
            trials.append(cost.Trial(method, "shared", ("shared",), "shared", {"shared": 15}, 30))
            k = str(i)
            trials.append(cost.Trial(method, k, (k,), k, {k: 25}, 25 + i))
        features = {k: ("entries", 1, 1) for k in ("shared", "0", "1", "2", "3")}
        with patch.object(cost, "collect", return_value=(trials, features, {})), patch.object(
                cost, "fit", wraps=cost.fit) as fit:
            cache = cost.compile_cache(Path("."), "aarch64")
        for i, call in enumerate(fit.call_args_list[:-1]):
            self.assertEqual({t.unique for t in call.args[0]}, set("0123") - {str(i)})
        self.assertEqual(cache["validation"]["distinct"], 5)
        self.assertGreaterEqual(cache["reserve"], 0)
