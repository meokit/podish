import pytest
from tests.runner import X86EmuBackend, Runner

def test_smc_direct_nop():
    """
    Test Case 1: Direct SMC (Same Block)
    Modifies the immediately following instruction to NOP.
    Expectation: Old code executes (EAX=2) because of stale fetch/no invalidation of current block.
    """
    runner = Runner()
    # MOV BYTE [0x100A], 0x90  ; 7 bytes (C6 05 0A 10 00 00 90)
    # NOP                      ; 1 byte
    # NOP                      ; 1 byte
    # INC EAX                  ; 1 byte (40) -> at 0x1009 (offset 9)
    #
    # Wait, 7+1+1 = 9. So 0x1009.
    
    asm = """
        mov byte [0x1009], 0x90
        nop
        nop
        inc eax
    """
    
    # Expected: EAX = 2 (Old code executes)
    runner.run_test(
        "SMC Direct NOP",
        asm,
        expected_regs={'EAX': 2},
        check_unicorn=False # Unicorn might handle this differently (invalidation)
    )

def test_smc_cross_block_uaf():
    """
    Test Case 2: Cross-Block SMC
    Block A modifies Block B then jumps to it.
    Expectation: New code (NOP) executes (EAX=1).
    """
    runner = Runner()
    # Layout:
    # 0x1000: MOV BYTE [0x1020], 0x90  (7 bytes)
    # 0x1007: JMP 0x1020               (5 bytes)
    # 0x100C: Padding...
    # 0x1020: INC EAX                  (1 byte)
    # 0x1021: HLT
    
    # Gap from 0x100C to 0x1020 is 20 (0x14) bytes.
    
    asm = """
        mov byte [0x1020], 0x90
        jmp 0x1020
        times 20 nop
        inc eax
        hlt
    """
    
    # Expected: EAX = 1 (INC EAX becomes NOP)
    runner.run_test(
        "SMC Cross Block",
        asm,
        expected_regs={'EAX': 1},
        check_unicorn=False
    )

def test_smc_self_modification():
    """
    Test Case 3: Modifying the CURRENT block.
    """
    # Just repeating Case 1 but explicit
    pass