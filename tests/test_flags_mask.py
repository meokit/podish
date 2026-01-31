
import pytest
from tests.runner import Runner

@pytest.mark.jump # Reusing jump marker or create new? Let's use no marker or unit.
def test_eflags_mask_if_protection():
    """Verify that IF (Interrupt Flag) cannot be cleared by POPFD in user mode."""
    runner = Runner()
    # Initial EFLAGS is 0x202 (IF=1, Reserved=1)
    # PUSH 0x00000000 (Try to clear all flags, including IF)
    # POPFD
    code = b'\x68\x00\x00\x00\x00\x9D'
    
    # Expectation:
    # Mask does NOT include IF (bit 9). So IF remains 1.
    # Mask includes arithmetic flags, so they should be cleared.
    # Reserved bit 1 is always 1.
    # Result: 0x202.
    runner.run_test_bytes("test_eflags_mask_if_protection", code, 
        expected_eflags=0x202,
        check_eflags_mask=0xFFFFFFFF, # Strict check including IF
        count=2
    )

def test_eflags_mask_allowed_bits():
    """Verify that arithmetic flags can be modified."""
    runner = Runner()
    # Set CF(0), PF(2), ZF(6), SF(7), OF(11).
    # Also set IF=1, Reserved=1 just in case (though mask protects them, input matching helps clarity)
    # Target value: 0x8C5 (Flags) | 0x202 (System) = 0xAC7.
    
    code = b'\x68\xC7\x0A\x00\x00\x9D' # PUSH 0xAC7; POPFD
    
    runner.run_test_bytes("test_eflags_mask_allowed_bits", code,
        expected_eflags=0xAC7,
        check_eflags_mask=0xFFFFFFFF,
        count=2
    )

def test_std_cld():
    """Verify STD/CLD work (Direction Flag is modifiable)."""
    runner = Runner()
    # STD -> DF=1 (Bit 10, 0x400)
    code = b'\xFD' 
    runner.run_test_bytes("test_std", code, expected_eflags=0x602, check_eflags_mask=0xFFFFFFFF)
    
    # CLD -> DF=0
    code2 = b'\xFD\xFC' 
    runner.run_test_bytes("test_cld", code2, expected_eflags=0x202, check_eflags_mask=0xFFFFFFFF, count=2)

def test_stc_clc_cmc():
    """Verify Carry Flag operations."""
    runner = Runner()
    # STC -> CF=1
    runner.run_test_bytes("test_stc", b'\xF9', expected_eflags=0x203, check_eflags_mask=0xFFFFFFFF)
    
    # CLC -> CF=0
    runner.run_test_bytes("test_clc", b'\xF9\xF8', expected_eflags=0x202, check_eflags_mask=0xFFFFFFFF, count=2)
    
    # CMC -> Toggle CF
    runner.run_test_bytes("test_cmc_1", b'\xF5', expected_eflags=0x203, check_eflags_mask=0xFFFFFFFF) # 0->1
    runner.run_test_bytes("test_cmc_2", b'\xF9\xF5', expected_eflags=0x202, check_eflags_mask=0xFFFFFFFF, count=2) # 1->0
