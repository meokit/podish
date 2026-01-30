from tests.runner import Runner
import binascii
import pytest

@pytest.mark.unit
def test_hooks():
    runner = Runner()
    
    # Test 1: Memory Read Check using A1 (MOV r32, m32) - wait, A1 is correct opcode for MOV EAX, moffs32
    # ID: 395 | mov | r32, m32 |
    # But I want common one. 8B (MODRM)
    # A1 is specialized mov eax, moffs. Is it implemented?
    # Rank 1: mov r32, m32 -> 8B likely.
    
    # Let's try 8B (MOV r32, m32)
    # 8B 05 00 20 00 00 -> MOV EAX, [0x2000]
    print("[*] Testing Memory Read Hook (8B - MOV EAX, [0x2000])")
    runner.run_test_bytes(
        name="Memory Read Test",
        code=binascii.unhexlify("8B0500200000"),
        initial_regs={'EAX': 0xDEADBEEF},
        expected_regs={'EAX': 0}, # Memory at 0x2000 is 0
        expected_read={0x2000: 0} # Expect read from 0x2000 with val 0
    )

    # Test 2: Memory Write Check using 89 (MOV m32, r32)
    # 89 05 00 20 00 00 -> MOV [0x2000], EAX
    print("[*] Testing Memory Write Hook (89 - MOV [0x2000], EAX)")
    runner.run_test_bytes(
        name="Memory Write Test",
        code=binascii.unhexlify("890500200000"),
        initial_regs={'EAX': 0x12345678},
        expected_write={0x2000: 0x12345678}
    )

    # Test 3: EFLAGS Check
    # ADD EAX, 1. EAX=0xFFFFFFFF. result 0.
    # Flags: CF=1, ZF=1, PF=1, AF=1?
    # 0xFF + 1 = 0x00 (Carry). Yes AF=1.
    # SF=0. OF=0 (Neg+Pos=Pos? No. -1 + 1 = 0. No Overflow).
    # ID: 0x83 / 0 -> ADD r/m32, imm8
    # 83 C0 01
    print("[*] Testing EFLAGS (ADD EAX, 1)")
    # Expected EFLAGS after ADD EAX, 1 where EAX=0xFFFFFFFF:
    # CF(bit 0) = 1 (carry out)
    # PF(bit 2) = 1 (even parity - result 0 has 0 bits)
    # AF(bit 4) = 1 (auxiliary carry - 0xF + 1 crosses nibble)
    # ZF(bit 6) = 1 (result is zero)
    # SF(bit 7) = 0 (result is positive)
    # OF(bit 11)= 0 (no signed overflow)
    # Value: 01010101 = 0x55
    # Note: Reserved bit 1 is not set when initial_eflags=0
    runner.run_test_bytes(
        name="EFLAGS Test",
        code=binascii.unhexlify("83C001"),
        initial_regs={'EAX': 0xFFFFFFFF},
        initial_eflags=0, # Start clean
        expected_regs={'EAX': 0},
        expected_eflags=0x55  # CF | PF | AF | ZF
    )
    
if __name__ == "__main__":
    test_hooks()
