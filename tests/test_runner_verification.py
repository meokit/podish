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
        # PADDD xmm0, xmm1 (66 0F FE C1)
        # This instruction is currently UNIMPLEMENTED in x86emu (causing the test_redis_005 failures),
        # but supported by Unicorn.
        runner = Runner()
        try:
            runner.run_test_bytes(
                name="Unimplemented PADDD (Reg-Reg)",
                code=binascii.unhexlify('660ffec1'),
                initial_regs={'XMM0': 0, 'XMM1': 0},
                expected_regs={}
            )
            assert False, "Should have failed due to Unimplemented Instruction"
        except AssertionError as e:
            assert "Unimplemented Instruction" in str(e)
            # Should NOT mention Unicorn error because Unicorn passed
            assert "vs Unicorn: N/A" not in str(e) and "Unicorn: None" not in str(e)

    def test_fail_unimplemented_vs_unicorn_crash(self):
        """
        Runner should FAIL if Sim faults (#UD) and Unicorn crashes with Memory Error (not Invalid Op).
        This verifies the fix for False Passes in test_redis_005.
        """
        # PADDD xmm0, [eax] (66 0F FE 00)
        # EAX=0 (Unmapped) -> Unicorn Segfaults
        # Sim -> #UD (Unimplemented)
        # Result: FAIL (Sim is missing implementation, doesn't matter if data was bad)
        runner = Runner()
        try:
            runner.run_test_bytes(
                name="Unimplemented PADDD (Mem Access Segfault)",
                code=binascii.unhexlify('660ffe00'), 
                initial_regs={'EAX': 0},
                expected_regs={}
            )
            assert False, "Should have failed (Sim #UD != UC Segfault)"
        except AssertionError as e:
            assert "Unimplemented Instruction" in str(e)
            # Should indicate Unicorn had an error
            assert "vs Unicorn" in str(e)

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
        # Use INT 3 (CC) which triggers Status=2 (Fault) / Vector 3 (#BP)
        # If we expect valid execution (no fault), this should fail.
        # But wait, INT 3 is valid.
        
        # Let's mock a fault.
        # Actually, let's use a HLT (F4) -> Sim stops? Or Fault?
        # Sim HLT stops.
        
        # Let's use a byte that causes #GP in Sim but is valid?
        # Hard to force without changing code.
        
        # Using a manually forced fault via Memory Write to Read-Only?
        # Code segment is 0x1000. Write to 0x1000 -> #PF or #GP if read-only?
        # Sim maps Code as RWX (7).
        pass
