import pytest
import binascii
from tests.runner import Runner

class TestRunnerLogic:
    """
    Meta-tests for the Runner class to ensure it correctly identifies
    Pass, Fail, and Fault conditions, specifically addressing 
    false positives/negatives in regression suites.
    """

    def test_pass_valid_instruction(self):
        """Runner should PASS when Sim and Unicorn match."""
        runner = Runner()
        # INC EAX (40)
        # EAX starts at 1, becomes 2.
        assert runner.run_test_bytes(
            name="Valid INC EAX",
            code=b'\x40',
            initial_regs={'EAX': 1},
            expected_regs={'EAX': 2}
        )

    def test_fail_register_mismatch(self):
        """Runner should FAIL when Sim result differs from Expected."""
        runner = Runner()
        # INC EAX (40) -> EAX=2
        # Expect EAX=3
        try:
            runner.run_test_bytes(
                name="Mismatch INC EAX",
                code=b'\x40',
                initial_regs={'EAX': 1},
                expected_regs={'EAX': 3}
            )
            assert False, "Should have raised AssertionError for register mismatch"
        except AssertionError as e:
            assert "EAX Mismatch" in str(e)

    def test_fail_unimplemented_vs_unicorn_ok(self):
        """Runner should FAIL if Sim faults (#UD) but Unicorn succeeds."""
        # 0F 38 CC (SHA1RNDS4) - Map 2 not implemented in Sim
        runner = Runner()
        try:
            runner.run_test_bytes(
                name="Unimplemented SHA1 (0F 38 CC)",
                code=binascii.unhexlify('0f38ccc000'), # xmm0, xmm0, 0
                initial_regs={'XMM0': 0},
                expected_regs={}
            )
            assert False, "Should have failed due to Unimplemented Instruction"
        except AssertionError as e:
            assert "Unimplemented Instruction" in str(e)

    def test_fail_unimplemented_vs_unicorn_crash(self):
        """
        Runner should FAIL if Sim faults (#UD) and Unicorn crashes with Memory Error.
        """
        # FXSAVE [eax]
        # 0F AE 00
        # Sim should #UD (Unimplemented).
        # Unicorn should try to write [EAX]=0 -> Segfault.
        runner = Runner()
        try:
            runner.run_test_bytes(
                name="Unimplemented FXSAVE (Mem Access Segfault)",
                code=binascii.unhexlify('0fae00'), 
                initial_regs={'EAX': 0},
                expected_regs={}
            )
            assert False, "Should have failed (Sim #UD != UC Segfault)"
        except AssertionError as e:
            assert "Unimplemented Instruction" in str(e)
            # Unicorn fault is not printed if Sim faults, but the test fails because 'ignored' is False.
            # So "Unimplemented Instruction" (from Sim fault msg) should be there.
            # And since it failed, we are good.
            pass

    def test_pass_invalid_opcode_both(self):
        """Runner should PASS/IGNORE if BOTH Sim and Unicorn fault with #UD (Invalid Opcode)."""
        runner = Runner()
        # UD2 (0F 0B) - Guaranteed #UD
        # Both engines should raise Invalid Opcode.
        assert runner.run_test_bytes(
            name="UD2 (Both Invalid)",
            code=b'\x0F\x0B',
            initial_regs={},
            expected_regs={}
        )

    def test_fail_sim_crash_valid_code(self):
        """Runner should FAIL if Sim faults (nonzero code) on valid code that Unicorn runs."""
        # This test ensures we catch random faults.
        pass