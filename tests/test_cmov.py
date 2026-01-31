
import pytest
from tests.runner import Runner

@pytest.mark.jump
def test_cmov_overflow():
    """Test CMOVO (OF=1) and CMOVNO (OF=0)"""
    runner = Runner()
    
    # 1. Test CMOVO (taken) and CMOVNO (not taken) when OF=1
    # Set OF=1: 0x7F + 0x01 (Signed Overflow)
    asm_of = """
    mov al, 0x7F
    add al, 0x01   ; AL=0x80, OF=1
    mov ebx, 0xDEADBEEF
    mov ecx, 0xCAFEBABE
    cmovo ebx, ecx ; Should move (OF=1)
    cmovno ebx, eax ; Should NOT move
    """
    runner.run_test("case_cmovo_taken", asm_of, 
        expected_regs={"EBX": 0xCAFEBABE}
    )

    # 2. Test CMOVO (not taken) and CMOVNO (taken) when OF=0
    asm_nof = """
    mov al, 0x01
    add al, 0x01   ; AL=0x02, OF=0
    mov ebx, 0xDEADBEEF
    mov ecx, 0xCAFEBABE
    cmovo ebx, ecx ; Should NOT move
    cmovno ebx, ecx ; Should move
    """
    runner.run_test("case_cmovo_not_taken", asm_nof,
        expected_regs={"EBX": 0xCAFEBABE}
    )

@pytest.mark.jump
def test_cmov_carry():
    """Test CMOVB (CF=1) and CMOVNB (CF=0)"""
    runner = Runner()
    
    # 1. CF=1
    asm_cf = """
    stc             ; CF=1
    mov ebx, 0x11111111
    mov ecx, 0x22222222
    cmovb ebx, ecx  ; Should move
    cmovnb ebx, eax ; Should NOT move
    """
    runner.run_test("case_cmovb_taken", asm_cf,
        expected_regs={"EBX": 0x22222222}
    )

    # 2. CF=0
    asm_ncf = """
    clc             ; CF=0
    mov ebx, 0x11111111
    mov ecx, 0x22222222
    cmovb ebx, ecx  ; Should NOT move
    cmovnb ebx, ecx ; Should move
    """
    runner.run_test("case_cmovb_not_taken", asm_ncf,
        expected_regs={"EBX": 0x22222222}
    )

@pytest.mark.jump
def test_cmov_zero():
    """Test CMOVZ/E and CMOVNZ/NE"""
    runner = Runner()
    
    # 1. ZF=1
    asm_z = """
    xor eax, eax    ; ZF=1
    mov ebx, 0x33333333
    mov ecx, 0x44444444
    cmovz ebx, ecx  ; Should move
    cmovnz ebx, eax ; Should NOT move
    """
    runner.run_test("case_cmovz_taken", asm_z,
        expected_regs={"EBX": 0x44444444}
    )

    # 2. ZF=0
    asm_nz = """
    mov eax, 1
    test eax, eax   ; ZF=0
    mov ebx, 0x33333333
    mov ecx, 0x44444444
    cmovz ebx, ecx  ; Should NOT move
    cmovnz ebx, ecx ; Should move
    """
    runner.run_test("case_cmovz_not_taken", asm_nz,
        expected_regs={"EBX": 0x44444444}
    )

@pytest.mark.jump
def test_cmov_sign():
    """Test CMOVS (SF=1) and CMOVNS (SF=0)"""
    runner = Runner()
    
    # 1. SF=1
    asm_s = """
    mov al, 0xFF
    test al, al     ; SF=1 (bit 7 set)
    mov ebx, 0x55555555
    mov ecx, 0x66666666
    cmovs ebx, ecx  ; Should move
    cmovns ebx, eax ; Should NOT move
    """
    runner.run_test("case_cmovs_taken", asm_s,
        expected_regs={"EBX": 0x66666666}
    )

    # 2. SF=0
    asm_ns = """
    mov al, 0x7F
    test al, al     ; SF=0
    mov ebx, 0x55555555
    mov ecx, 0x66666666
    cmovs ebx, ecx  ; Should NOT move
    cmovns ebx, ecx ; Should move
    """
    runner.run_test("case_cmovs_not_taken", asm_ns,
        expected_regs={"EBX": 0x66666666}
    )

@pytest.mark.jump
def test_cmov_parity():
    """Test CMOVP (PF=1) and CMOVNP (PF=0)"""
    runner = Runner()
    
    # 1. PF=1 (Even parity)
    # 0x03 = 0000 0011 (2 bits set -> Even)
    asm_p = """
    mov al, 0x03
    add al, 0       ; Update flags
    mov ebx, 0x77777777
    mov ecx, 0x88888888
    cmovp ebx, ecx  ; Should move
    cmovnp ebx, eax ; Should NOT move
    """
    runner.run_test("case_cmovp_taken", asm_p,
        expected_regs={"EBX": 0x88888888}
    )

    # 2. PF=0 (Odd parity)
    # 0x01 = 0000 0001 (1 bit set -> Odd)
    asm_np = """
    mov al, 0x01
    add al, 0       ; Update flags
    mov ebx, 0x77777777
    mov ecx, 0x88888888
    cmovp ebx, ecx  ; Should NOT move
    cmovnp ebx, ecx ; Should move
    """
    runner.run_test("case_cmovp_not_taken", asm_np,
        expected_regs={"EBX": 0x88888888}
    )

@pytest.mark.jump
def test_cmov_signed_comparisons():
    """Test CMOVGE, CMOVLE, CMOVG, CMOVL"""
    runner = Runner()
    
    # Greater (Signed)
    # 2 > 1
    asm_g = """
    mov eax, 2
    cmp eax, 1      ; SF=0, OF=0, ZF=0 => G=1, L=0
    mov ebx, 100
    mov ecx, 200
    cmovg ebx, ecx  ; Move
    cmovle ebx, eax ; No move
    """
    runner.run_test("case_cmovg", asm_g, expected_regs={"EBX": 200})
    
    # Less (Signed)
    # 1 < 2
    asm_l = """
    mov eax, 1
    cmp eax, 2      ; SF=1, OF=0, ZF=0 => L=1, G=0
    mov ebx, 100
    mov ecx, 200
    cmovl ebx, ecx  ; Move
    cmovge ebx, eax ; No move
    """
    runner.run_test("case_cmovl", asm_l, expected_regs={"EBX": 200})

@pytest.mark.jump
def test_cmov_opsize_16():
    """Test 16-bit CMOV logic"""
    runner = Runner()
    
    # CMOVZ 16-bit
    # We want to verify that only low 16-bit are modified
    asm_16 = """
    xor eax, eax    ; ZF=1
    mov ebx, 0xDEADBEEF
    mov ecx, 0x0000CAFE
    cmovz bx, cx    ; Should move CX to BX (CAFE to BEEF)
                    ; EBX should become 0xDEADCAFE
    """
    runner.run_test("case_cmov16", asm_16, 
        expected_regs={"EBX": 0xDEADCAFE}
    )

@pytest.mark.jump
def test_cmov_memory_source():
    """Test CMOV from memory"""
    runner = Runner()
    
    # cmovz ebx, [addr]
    asm_mem = """
    xor eax, eax    ; ZF=1
    mov ebx, 0x11111111
    cmovz ebx, [0x2000]
    """
    runner.run_test("case_cmov_mem", asm_mem, 
        expected_regs={"EBX": 0x12345678},
        expected_read={0x2000: 0x12345678}
    )
