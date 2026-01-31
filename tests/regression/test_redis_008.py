# Redis Regression Test Batch 008
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_379_xor_m32_r32():
    """Test: ID_379: xor dword ptr [ebp - 0x2c], edx"""
    runner = Runner()
    # Raw: 3155d4
    # Instruction: xor r/m32, r32
    # ModR/M: 01 010 101 -> 0x55 (Disp8, Reg=EDX, R/M=EBP)
    # Disp: -0x2C -> 0xD4
    # Result: 0x01010101 ^ 0xFFFFFFFF = 0xFEFEFEFE
    # Flags: SF=1 (bit 31 is 1). 
    # PF Check (Low Byte 0xFE -> 1111 1110 -> 7 bits set -> Odd Parity -> PF=0).
    assert runner.run_test_bytes(
        name='ID_379: xor dword ptr [ebp - 0x2c], edx',
        code=binascii.unhexlify('3155d4'),
        initial_regs={'EBP': 0x8000, 'EDX': 0xFFFFFFFF},
        expected_read={0x7FD4: 0x01010101},
        expected_write={0x7FD4: 0xFEFEFEFE},
        initial_eflags=0,
        expected_eflags=0x80 # SF=1, PF=0 (Fixed: 0xFE is odd parity)
    )

@pytest.mark.regression
def test_id_331_xor_r16_m16():
    """Test: ID_331: xor ax, word ptr [ecx + ebx*2]"""
    runner = Runner()
    # Raw: 6633845900000000
    # Prefix: 66 (16-bit)
    # Op: 33 (xor r16, r/m16)
    # ModR/M: 10 000 100 -> 0x84 (Disp32, Reg=AX, R/M=SIB)
    # SIB: 01 011 001 -> 0x59 (Scale=2, Index=EBX, Base=ECX)
    # Disp: 0x00000000
    assert runner.run_test_bytes(
        name='ID_331: xor ax, word ptr [ecx + ebx*2]',
        code=binascii.unhexlify('6633845900000000'),
        initial_regs={'ECX': 0x2000, 'EBX': 0x10, 'EAX': 0x5555},
        expected_read={0x2020: 0xAAAA},
        expected_regs={'EAX': 0xFFFF},
        initial_eflags=0,
        expected_eflags=0x80 | 0x4 # SF=1, PF=1 (Low byte 0xFF is even parity)
    )

@pytest.mark.regression
def test_id_232_xor_r32_imm32():
    """Test: ID_232: xor esi, 3"""
    runner = Runner()
    # Raw: 83f603
    # Op: 83 (Arithmetic imm8 sign-extended) /6 (XOR)
    # ModR/M: 11 110 110 -> 0xF6 (Reg Mode, ESI)
    # Imm8: 03
    # Result: -1 ^ 3 = -4 (0xFFFFFFFC)
    # PF Check (Low byte 0xFC -> 1111 1100 -> 6 bits set -> Even -> PF=1)
    assert runner.run_test_bytes(
        name='ID_232: xor esi, 3',
        code=binascii.unhexlify('83f603'),
        initial_regs={'ESI': 0xFFFFFFFF},
        expected_regs={'ESI': 0xFFFFFFFC},
        initial_eflags=0,
        expected_eflags=0x80 | 0x4 # SF=1, PF=1
    )

@pytest.mark.regression
def test_id_151_xor_r32_m32():
    """Test: ID_151: xor ecx, dword ptr [ebp - 0x24]"""
    runner = Runner()
    # Raw: 334ddc
    # Op: 33 (xor r32, r/m32)
    # ModR/M: 01 001 101 -> 0x4D (Disp8, ECX, EBP)
    # Disp: -0x24 -> 0xDC
    assert runner.run_test_bytes(
        name='ID_151: xor ecx, dword ptr [ebp - 0x24]',
        code=binascii.unhexlify('334ddc'),
        initial_regs={'ECX': 0xDEADBEEF, 'EBP': 0x8000},
        expected_read={0x7FDC: 0xDEADBEEF},
        expected_regs={'ECX': 0},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1 (0x00 is even parity)
    )

@pytest.mark.regression
def test_id_23_xor_r32_r32():
    """Test: ID_23: xor edi, edi"""
    runner = Runner()
    # Raw: 31ff
    # Op: 31 (xor r/m32, r32)
    # ModR/M: 11 111 111 -> 0xFF (EDI, EDI)
    assert runner.run_test_bytes(
        name='ID_23: xor edi, edi',
        code=binascii.unhexlify('31ff'),
        initial_regs={'EDI': 0x12345678},
        expected_regs={'EDI': 0},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_217_xor_r8_imm8():
    """Test: ID_217: xor al, 1"""
    runner = Runner()
    # Raw: 3401
    # Op: 34 (xor al, imm8)
    assert runner.run_test_bytes(
        name='ID_217: xor al, 1',
        code=binascii.unhexlify('3401'),
        initial_regs={'EAX': 0x00},
        expected_regs={'EAX': 1},
        initial_eflags=0,
        expected_eflags=0 # PF=0 (0x01 is odd parity)
    )

@pytest.mark.regression
def test_id_293_xor_r8_m8():
    """Test: ID_293: xor cl, byte ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 324db8
    # Op: 32 (xor r8, r/m8)
    # ModR/M: 01 001 101 -> 0x4D (Disp8, CL, EBP)
    # Disp: -0x48 -> 0xB8
    # Result: 0xFF ^ 0xF0 = 0x0F
    # PF Check (0x0F -> 0000 1111 -> 4 bits -> Even -> PF=1)
    assert runner.run_test_bytes(
        name='ID_293: xor cl, byte ptr [ebp - 0x48]',
        code=binascii.unhexlify('324db8'),
        initial_regs={'ECX': 0xFF, 'EBP': 0x8000},
        expected_read={0x7FB8: 0xF0},
        expected_regs={'ECX': 0x0F},
        initial_eflags=0,
        expected_eflags=0x4 # PF=1
    )

@pytest.mark.regression
def test_id_294_xor_r8_r8():
    """Test: ID_294: xor al, cl"""
    runner = Runner()
    # Raw: 30c8
    # Op: 30 (xor r/m8, r8)
    # ModR/M: 11 001 000 -> 0xC8 (Src:CL(1), Dest:AL(0))
    # Result: 0xAA ^ 0x55 = 0xFF
    # PF Check (0xFF -> 8 bits -> Even -> PF=1)
    assert runner.run_test_bytes(
        name='ID_294: xor al, cl',
        code=binascii.unhexlify('30c8'),
        initial_regs={'EAX': 0xAA, 'ECX': 0x55},
        expected_regs={'EAX': 0xFF},
        initial_eflags=0,
        expected_eflags=0x80 | 0x4 # SF=1, PF=1
    )

@pytest.mark.regression
def test_id_244_xorpd_r128_r128():
    """Test: ID_244: xorpd xmm0, xmm0"""
    runner = Runner()
    # Raw: 660f57c0 (SSE2)
    val_xmm0 = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_244: xorpd xmm0, xmm0',
        code=binascii.unhexlify('660f57c0'),
        initial_regs={'XMM0': val_xmm0},
        expected_regs={'XMM0': binascii.unhexlify('00000000000000000000000000000000')}
    )

@pytest.mark.regression
def test_id_237_xorps_r128_m128():
    """Test: ID_237: xorps xmm0, xmmword ptr [ebp - 0x28]"""
    runner = Runner()
    # Raw: 0f5745d8 (SSE)
    # ModR/M: 01 000 101 -> 0x45 (Disp8, XMM0, EBP)
    # Disp: -0x28 -> 0xD8
    val_xmm0 = binascii.unhexlify('FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF')
    val_mem = binascii.unhexlify('FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF')
    assert runner.run_test_bytes(
        name='ID_237: xorps xmm0, xmmword ptr [ebp - 0x28]',
        code=binascii.unhexlify('0f5745d8'),
        initial_regs={'EBP': 0x8000, 'XMM0': val_xmm0},
        expected_read={0x7FD8: int.from_bytes(val_mem, 'little')},
        expected_regs={'XMM0': binascii.unhexlify('00000000000000000000000000000000')}
    )

@pytest.mark.regression
def test_id_109_xorps_r128_r128():
    """Test: ID_109: xorps xmm0, xmm0"""
    runner = Runner()
    # Raw: 0f57c0
    val_xmm0 = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_109: xorps xmm0, xmm0',
        code=binascii.unhexlify('0f57c0'),
        initial_regs={'XMM0': val_xmm0},
        expected_regs={'XMM0': binascii.unhexlify('00000000000000000000000000000000')}
    )