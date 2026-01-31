# Redis Regression Test Batch 007
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
# FIXED BY GEMINI: Corrected PF flags, SHLD logic, and SSE unpack byte ordering.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_131_shl_r32_r8():
    """Test: ID_131: shl edx, cl"""
    runner = Runner()
    # Raw: d3e2
    assert runner.run_test_bytes(
        name='ID_131: shl edx, cl',
        code=binascii.unhexlify('d3e2'),
        initial_regs={'EDX': 0x00000001, 'ECX': 4},
        expected_regs={'EDX': 0x00000010},
        initial_eflags=0,
        # FIX: Result 0x10 is binary 00010000. 1 bit set -> Odd Parity -> PF=0.
        expected_eflags=0 
    )

@pytest.mark.regression
def test_id_224_shl_r8_imm8():
    """Test: ID_224: shl al, 2"""
    runner = Runner()
    # Raw: c0e002
    assert runner.run_test_bytes(
        name='ID_224: shl al, 2',
        code=binascii.unhexlify('c0e002'),
        initial_regs={'EAX': 0x40},
        expected_regs={'EAX': 0}, # 0x40 << 2 = 0x100 -> 0x00
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 | 0x1 # ZF=1, PF=1, CF=1 (last bit shifted out was bit 7 of 0x80? No, shift 2. Bit 7 of 0x40 is 0. Shift 1: 0x80, CF=0. Shift 2: 0x00, CF=1)
    )

@pytest.mark.regression
def test_id_300_shl_r8_r8():
    """Test: ID_300: shl al, cl"""
    runner = Runner()
    # Raw: d2e0
    assert runner.run_test_bytes(
        name='ID_300: shl al, cl',
        code=binascii.unhexlify('d2e0'),
        initial_regs={'EAX': 0x01, 'ECX': 7},
        expected_regs={'EAX': 0x80},
        initial_eflags=0,
        # FIX: Result 0x80 (10000000) has 1 bit set -> Odd Parity -> PF=0.
        expected_eflags=0x80 # SF=1
    )

@pytest.mark.regression
def test_id_145_shld_m32_r32_imm8():
    """Test: ID_145: shld dword ptr [ebp - 0x10], esi, 1"""
    runner = Runner()
    # Raw: 0fa475f001
    assert runner.run_test_bytes(
        name='ID_145: shld dword ptr [ebp - 0x10], esi, 1',
        code=binascii.unhexlify('0fa475f001'),
        initial_regs={'EBP': 0x8000, 'ESI': 0x80000000},
        expected_read={0x7FF0: 0x7FFFFFFF},
        # FIX: (7FFFFFFF << 1) | (80000000 >> 31).
        # 7FFFFFFF << 1 = FFFFFFFE.
        # 80000000 >> 31 = 1.
        # FFFFFFFE | 1 = FFFFFFFF.
        expected_write={0x7FF0: 0xFFFFFFFF}, 
        initial_eflags=0,
        expected_eflags=0x80 | 0x800 | 0x4 # SF=1, OF=1, PF=1 (0xFF has 8 bits set -> Even Parity)
    )

@pytest.mark.regression
def test_id_154_shld_r32_r32_imm8():
    """Test: ID_154: shld edi, esi, 1"""
    runner = Runner()
    # Raw: 0fa4f701
    assert runner.run_test_bytes(
        name='ID_154: shld edi, esi, 1',
        code=binascii.unhexlify('0fa4f701'),
        initial_regs={'EDI': 0x00000000, 'ESI': 0x00000001},
        expected_regs={'EDI': 0x00000000}, # (0 << 1) | (1 >> 31) = 0
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_288_shld_r32_r32_r8():
    """Test: ID_288: shld edi, esi, cl"""
    runner = Runner()
    # Raw: 0fa5f7
    assert runner.run_test_bytes(
        name='ID_288: shld edi, esi, cl',
        code=binascii.unhexlify('0fa5f7'),
        initial_regs={'EDI': 0x00000001, 'ESI': 0xFFFFFFFF, 'ECX': 32},
        expected_regs={'EDI': 0x00000001}, # SHLD with count 32 is undefined or no-op in some contexts, but usually count is MOD 32. 32 MOD 32 = 0? 
        # Actually SHLD count is MOD 32. If count is 0, no change.
        initial_eflags=0,
        expected_eflags=0
    )

@pytest.mark.regression
def test_id_229_shr_m32_imm32():
    """Test: ID_229: shr dword ptr [ebp - 0x20], 1"""
    runner = Runner()
    # Raw: d16de0
    assert runner.run_test_bytes(
        name='ID_229: shr dword ptr [ebp - 0x20], 1',
        code=binascii.unhexlify('d16de0'),
        initial_regs={'EBP': 0x8000},
        expected_read={0x7FE0: 0x00000003},
        expected_write={0x7FE0: 0x00000001},
        initial_eflags=0,
        expected_eflags=1 # CF=1 (bit 0 was 1)
    )

@pytest.mark.regression
def test_id_333_shr_m32_imm8():
    """Test: ID_333: shr dword ptr [ebp - 0x14], 3"""
    runner = Runner()
    # Raw: c16dec03
    assert runner.run_test_bytes(
        name='ID_333: shr dword ptr [ebp - 0x14], 3',
        code=binascii.unhexlify('c16dec03'),
        initial_regs={'EBP': 0x8000},
        expected_read={0x7FEC: 0x00000010},
        expected_write={0x7FEC: 0x00000002},
        initial_eflags=0,
        # FIX: Result 0x02 (00000010) has 1 bit set -> Odd Parity -> PF=0.
        expected_eflags=0 
    )

@pytest.mark.regression
def test_id_434_shr_m32_r8():
    """Test: ID_434: shr dword ptr [ebp - 0x20], cl"""
    runner = Runner()
    # Raw: d36de0
    assert runner.run_test_bytes(
        name='ID_434: shr dword ptr [ebp - 0x20], cl',
        code=binascii.unhexlify('d36de0'),
        initial_regs={'EBP': 0x8000, 'ECX': 4},
        expected_read={0x7FE0: 0x000000F0},
        expected_write={0x7FE0: 0x0000000F},
        initial_eflags=0,
        expected_eflags=0x4 # PF=1 (0x0F = 00001111, 4 bits set -> even parity)
    )

@pytest.mark.regression
def test_id_228_shr_r32_imm32():
    """Test: ID_228: shr ecx, 1"""
    runner = Runner()
    # Raw: d1e9
    assert runner.run_test_bytes(
        name='ID_228: shr ecx, 1',
        code=binascii.unhexlify('d1e9'),
        initial_regs={'ECX': 0x80000000},
        expected_regs={'ECX': 0x40000000},
        initial_eflags=0,
        expected_eflags=0x800 | 0x4 # OF=1, PF=1 (00 even)
    )

@pytest.mark.regression
def test_id_72_shr_r32_imm8():
    """Test: ID_72: shr ecx, 3"""
    runner = Runner()
    # Raw: c1e903
    assert runner.run_test_bytes(
        name='ID_72: shr ecx, 3',
        code=binascii.unhexlify('c1e903'),
        initial_regs={'ECX': 0xFFFFFFFF},
        expected_regs={'ECX': 0x1FFFFFFF},
        initial_eflags=0,
        expected_eflags=0x1 | 0x4 # CF=1 (bit 2 was 1), PF=1 (0xFF -> Even)
    )

@pytest.mark.regression
def test_id_213_shr_r32_r8():
    """Test: ID_213: shr eax, cl"""
    runner = Runner()
    # Raw: d3e8
    assert runner.run_test_bytes(
        name='ID_213: shr eax, cl',
        code=binascii.unhexlify('d3e8'),
        initial_regs={'EAX': 0x00000010, 'ECX': 4},
        expected_regs={'EAX': 0x00000001},
        initial_eflags=0,
        expected_eflags=0 # PF=0 (1 has odd parity)
    )

@pytest.mark.regression
def test_id_197_shr_r8_imm8():
    """Test: ID_197: shr bl, 3"""
    runner = Runner()
    # Raw: c0eb03
    assert runner.run_test_bytes(
        name='ID_197: shr bl, 3',
        code=binascii.unhexlify('c0eb03'),
        initial_regs={'EBX': 0x07},
        expected_regs={'EBX': 0x00},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 | 0x1 # ZF=1, PF=1, CF=1 (bit 2 was 1)
    )

@pytest.mark.regression
def test_id_210_shrd_r32_r32_imm8():
    """Test: ID_210: shrd ecx, eax, 1"""
    runner = Runner()
    # Raw: 0facc101
    assert runner.run_test_bytes(
        name='ID_210: shrd ecx, eax, 1',
        code=binascii.unhexlify('0facc101'),
        initial_regs={'ECX': 0x00000000, 'EAX': 0x00000001},
        expected_regs={'ECX': 0x80000000},
        initial_eflags=0,
        expected_eflags=0x80 | 0x800 | 0x4 # SF=1, OF=1, PF=1 (0x00 -> Even)
    )

@pytest.mark.regression
def test_id_285_shrd_r32_r32_r8():
    """Test: ID_285: shrd edi, ebx, cl"""
    runner = Runner()
    # Raw: 0faddf
    assert runner.run_test_bytes(
        name='ID_285: shrd edi, ebx, cl',
        code=binascii.unhexlify('0faddf'),
        initial_regs={'EDI': 0x80000000, 'EBX': 0xFFFFFFFF, 'ECX': 0x8},
        expected_regs={'EDI': 0xFF800000},
        initial_eflags=0,
        expected_eflags=0x80 | 0x4
    )

@pytest.mark.regression
def test_id_395_shufpd_r128_r128_imm8():
    """Test: ID_395: shufpd xmm3, xmm4, 2"""
    runner = Runner()
    # Raw: 660fc6dc02
    # imm8=2: 00000010b.
    # bit 0 = 0: xmm3[63:0]   <- xmm3[63:0]
    # bit 1 = 1: xmm3[127:64] <- xmm4[127:64]
    val_xmm3 = binascii.unhexlify('00112233445566778899aabbccddeeff')
    val_xmm4 = binascii.unhexlify('ffeeddccbbaa99887766554433221100')
    expected_val = binascii.unhexlify('00112233445566777766554433221100')
    assert runner.run_test_bytes(
        name='ID_395: shufpd xmm3, xmm4, 2',
        code=binascii.unhexlify('660fc6dc02'),
        initial_regs={'XMM3': val_xmm3, 'XMM4': val_xmm4},
        expected_regs={'XMM3': expected_val}
    )

@pytest.mark.regression
def test_id_270_shufps_r128_r128_imm8():
    """Test: ID_270: shufps xmm6, xmm5, 0xe4"""
    runner = Runner()
    # Raw: 0fc6f5e4
    # 0xe4 = 11 10 01 00b
    # DEST[31:0]   <- DEST[elem 0]
    # DEST[63:32]  <- DEST[elem 1]
    # DEST[95:64]  <- SRC[elem 2]
    # DEST[127:96] <- SRC[elem 3]
    # Since 0xe4 is (3,2,1,0), it's basically a no-op if we structure the data correctly.
    # elem 0: 0, elem 1: 1, elem 2: 2, elem 3: 3
    val_xmm6 = binascii.unhexlify('00000001000000020000000300000004')
    val_xmm5 = binascii.unhexlify('00000005000000060000000700000008')
    # expected: xmm6[0], xmm6[1], xmm5[2], xmm5[3]
    # xmm6[0] = 00000001
    # xmm6[1] = 00000002
    # xmm5[2] = 00000007
    # xmm5[3] = 00000008
    expected_val = binascii.unhexlify('00000001000000020000000700000008')
    assert runner.run_test_bytes(
        name='ID_270: shufps xmm6, xmm5, 0xe4',
        code=binascii.unhexlify('0fc6f5e4'),
        initial_regs={'XMM6': val_xmm6, 'XMM5': val_xmm5},
        expected_regs={'XMM6': expected_val}
    )

@pytest.mark.regression
def test_id_433_sqrtsd_r128_r128():
    """Test: ID_433: sqrtsd xmm0, xmm0"""
    runner = Runner()
    # Raw: f20f51c0
    val_xmm0 = binascii.unhexlify('00000000000010400000000000000000') # 4.0
    expected_val = binascii.unhexlify('00000000000000400000000000000000') # 2.0
    assert runner.run_test_bytes(
        name='ID_433: sqrtsd xmm0, xmm0',
        code=binascii.unhexlify('f20f51c0'),
        initial_regs={'XMM0': val_xmm0},
        expected_regs={'XMM0': expected_val}
    )

@pytest.mark.regression
def test_id_243_sub_m32_imm32():
    """Test: ID_243: sub dword ptr [ebp - 0x28], 1"""
    runner = Runner()
    # Raw: 836dd801
    assert runner.run_test_bytes(
        name='ID_243: sub dword ptr [ebp - 0x28], 1',
        code=binascii.unhexlify('836dd801'),
        initial_regs={'EBP': 0x8000},
        expected_read={0x7FD8: 1},
        expected_write={0x7FD8: 0},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_141_sub_m32_r32():
    """Test: ID_141: sub dword ptr [edx + 0x78c], ecx"""
    runner = Runner()
    # Raw: 298a8c070000
    assert runner.run_test_bytes(
        name='ID_141: sub dword ptr [edx + 0x78c], ecx',
        code=binascii.unhexlify('298a8c070000'),
        initial_regs={'EDX': 0x2000, 'ECX': 0x1},
        expected_read={0x278C: 0x1},
        expected_write={0x278C: 0},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_6_sub_r32_imm32():
    """Test: ID_6: sub esp, 0xc"""
    runner = Runner()
    # Raw: 83ec0c
    assert runner.run_test_bytes(
        name='ID_6: sub esp, 0xc',
        code=binascii.unhexlify('83ec0c'),
        initial_regs={'ESP': 0x8000},
        expected_regs={'ESP': 0x7FF4}
    )

@pytest.mark.regression
def test_id_114_sub_r32_m32():
    """Test: ID_114: sub eax, dword ptr [ebp - 0x14]"""
    runner = Runner()
    # Raw: 2b45ec
    assert runner.run_test_bytes(
        name='ID_114: sub eax, dword ptr [ebp - 0x14]',
        code=binascii.unhexlify('2b45ec'),
        initial_regs={'EBP': 0x8000, 'EAX': 0x10},
        expected_read={0x7FEC: 0x5},
        expected_regs={'EAX': 0xB},
        initial_eflags=0,
        # FIX: Result 0xB (Odd). PF=0. AF=1 (0x10 - 0x05 -> borrow bit 3).
        expected_eflags=0x10 # AF=1
    )

@pytest.mark.regression
def test_id_77_sub_r32_r32():
    """Test: ID_77: sub esi, eax"""
    runner = Runner()
    # Raw: 29c6
    assert runner.run_test_bytes(
        name='ID_77: sub esi, eax',
        code=binascii.unhexlify('29c6'),
        initial_regs={'ESI': 0x100, 'EAX': 0x101},
        expected_regs={'ESI': 0xFFFFFFFF},
        initial_eflags=0,
        # 0x100 - 0x101 = -1 (FF..).
        # Byte: 00 - 01. Borrow from bit 3? 0000 - 0001. Yes (0 - 1). AF=1.
        expected_eflags=0x80 | 0x4 | 0x1 | 0x10 # SF=1, PF=1, CF=1, AF=1
    )

@pytest.mark.regression
def test_id_348_sub_r8_m8():
    """Test: ID_348: sub al, byte ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 2a45f0
    assert runner.run_test_bytes(
        name='ID_348: sub al, byte ptr [ebp - 0x10]',
        code=binascii.unhexlify('2a45f0'),
        initial_regs={'EAX': 0x5, 'EBP': 0x8000},
        expected_read={0x7FF0: 0x5},
        expected_regs={'EAX': 0},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_299_sub_r8_r8():
    """Test: ID_299: sub cl, dl"""
    runner = Runner()
    # Raw: 28d1
    assert runner.run_test_bytes(
        name='ID_299: sub cl, dl',
        code=binascii.unhexlify('28d1'),
        initial_regs={'ECX': 0x01, 'EDX': 0x02},
        expected_regs={'ECX': 0xFF},
        initial_eflags=0,
        # 1 - 2 = -1 (FF).
        # 01 - 02. 0001 - 0010.
        # Bit 0-0=1. Bit 1-1=1 (Borrow). Bit 2,3...
        # AF is borrow out of bit 3. 0001 - 0010. 1 < 2. Borrow flows all the way.
        # So AF=1.
        expected_eflags=0x80 | 0x4 | 0x1 | 0x10 # SF=1, PF=1, CF=1, AF=1
    )

@pytest.mark.regression
def test_id_385_subpd_r128_m128():
    """Test: ID_385: subpd xmm5, xmmword ptr [ecx]"""
    runner = Runner()
    # Raw: 660f5ca900000000
    val_xmm5 = binascii.unhexlify('00000000000014400000000000001C40') # 5.0, 7.0
    val_mem = binascii.unhexlify('00000000000000400000000000001040') # 2.0, 4.0
    expected_val = binascii.unhexlify('00000000000008400000000000000840') # 3.0, 3.0
    assert runner.run_test_bytes(
        name='ID_385: subpd xmm5, xmmword ptr [ecx]',
        code=binascii.unhexlify('660f5ca900000000'),
        initial_regs={'ECX': 0x2000, 'XMM5': val_xmm5},
        expected_read={0x2000: int.from_bytes(val_mem, 'little')},
        expected_regs={'XMM5': expected_val}
    )

@pytest.mark.regression
def test_id_387_subpd_r128_r128():
    """Test: ID_387: subpd xmm6, xmm5"""
    runner = Runner()
    # Raw: 660f5cf5
    val_xmm6 = binascii.unhexlify('00000000000014400000000000001C40') # 5.0, 7.0
    val_xmm5 = binascii.unhexlify('00000000000000400000000000001040') # 2.0, 4.0
    expected_val = binascii.unhexlify('00000000000008400000000000000840') # 3.0, 3.0
    assert runner.run_test_bytes(
        name='ID_387: subpd xmm6, xmm5',
        code=binascii.unhexlify('660f5cf5'),
        initial_regs={'XMM6': val_xmm6, 'XMM5': val_xmm5},
        expected_regs={'XMM6': expected_val}
    )

@pytest.mark.regression
def test_id_421_subsd_r128_m64():
    """Test: ID_421: subsd xmm0, qword ptr [edx]"""
    runner = Runner()
    # Raw: f20f5c8200000000
    val_xmm0 = binascii.unhexlify('00000000000014400000000000000000') # 5.0
    val_mem = binascii.unhexlify('00000000000000400000000000000000') # 2.0
    expected_val = binascii.unhexlify('00000000000008400000000000000000') # 3.0
    assert runner.run_test_bytes(
        name='ID_421: subsd xmm0, qword ptr [edx]',
        code=binascii.unhexlify('f20f5c8200000000'),
        initial_regs={'EDX': 0x2000, 'XMM0': val_xmm0},
        expected_read={0x2000: int.from_bytes(val_mem, 'little')},
        expected_regs={'XMM0': expected_val}
    )

@pytest.mark.regression
def test_id_247_subsd_r128_r128():
    """Test: ID_247: subsd xmm0, xmm1"""
    runner = Runner()
    # Raw: f20f5cc1
    val_xmm0 = binascii.unhexlify('00000000000014400000000000000000') # 5.0
    val_xmm1 = binascii.unhexlify('00000000000000400000000000000000') # 2.0
    expected_val = binascii.unhexlify('00000000000008400000000000000000') # 3.0
    assert runner.run_test_bytes(
        name='ID_247: subsd xmm0, xmm1',
        code=binascii.unhexlify('f20f5cc1'),
        initial_regs={'XMM0': val_xmm0, 'XMM1': val_xmm1},
        expected_regs={'XMM0': expected_val}
    )

@pytest.mark.regression
def test_id_249_test_m32_imm32():
    """Test: ID_249: test dword ptr [ecx + 0x10], 0xfffc000"""
    runner = Runner()
    # Raw: f7411000c0ff0f
    assert runner.run_test_bytes(
        name='ID_249: test dword ptr [ecx + 0x10], 0xfffc000',
        code=binascii.unhexlify('f7411000c0ff0f'),
        initial_regs={'ECX': 0x2000},
        expected_read={0x2010: 0x0004000},
        initial_eflags=0,
        expected_eflags=0x4 # PF=1 (0x4000 & 0xfffc000 = 0x4000. 1 bit set -> odd parity? No, PF is based on low 8 bits. 0x00 has 0 bits -> even parity -> PF=1)
    )

@pytest.mark.regression
def test_id_118_test_m32_r32():
    """Test: ID_118: test dword ptr [eax + 0x58], edi"""
    runner = Runner()
    # Raw: 857858
    assert runner.run_test_bytes(
        name='ID_118: test dword ptr [eax + 0x58], edi',
        code=binascii.unhexlify('857858'),
        initial_regs={'EAX': 0x2000, 'EDI': 0x01},
        expected_read={0x2058: 0x02},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1 (Result 0)
    )

@pytest.mark.regression
def test_id_82_test_m8_imm8():
    """Test: ID_82: test byte ptr [ecx + esi*2], 8"""
    runner = Runner()
    # Raw: f6047108
    assert runner.run_test_bytes(
        name='ID_82: test byte ptr [ecx + esi*2], 8',
        code=binascii.unhexlify('f6047108'),
        initial_regs={'ECX': 0x2000, 'ESI': 0x10},
        expected_read={0x2020: 0x08},
        initial_eflags=0,
        # FIX: Result 0x08 (00001000) has 1 bit set -> Odd Parity -> PF=0.
        expected_eflags=0 
    )

@pytest.mark.regression
def test_id_260_test_m8_r8():
    """Test: ID_260: test byte ptr [ebp - 0x14], cl"""
    runner = Runner()
    # Raw: 844dec
    assert runner.run_test_bytes(
        name='ID_260: test byte ptr [ebp - 0x14], cl',
        code=binascii.unhexlify('844dec'),
        initial_regs={'EBP': 0x8000, 'ECX': 0xFF},
        expected_read={0x7FEC: 0x01},
        initial_eflags=0,
        expected_eflags=0 # PF=0 (Result 1 -> 00000001 -> 1 bit -> odd -> PF=0)
    )

@pytest.mark.regression
def test_id_158_test_r16_r16():
    """Test: ID_158: test cx, cx"""
    runner = Runner()
    # Raw: 6685c9
    assert runner.run_test_bytes(
        name='ID_158: test cx, cx',
        code=binascii.unhexlify('6685c9'),
        initial_regs={'ECX': 0x0101},
        initial_eflags=0,
        # FIX: Low byte 0x01 has 1 bit set -> Odd Parity -> PF=0.
        expected_eflags=0 
    )

@pytest.mark.regression
def test_id_163_test_r32_imm32():
    """Test: ID_163: test edx, 0x200"""
    runner = Runner()
    # Raw: f7c200020000
    assert runner.run_test_bytes(
        name='ID_163: test edx, 0x200',
        code=binascii.unhexlify('f7c200020000'),
        initial_regs={'EDX': 0x200},
        initial_eflags=0,
        expected_eflags=0x4 # PF=1 (Result 0x200, low 8 bits 0 -> even -> PF=1)
    )

@pytest.mark.regression
def test_id_13_test_r32_r32():
    """Test: ID_13: test eax, eax"""
    runner = Runner()
    # Raw: 85c0
    assert runner.run_test_bytes(
        name='ID_13: test eax, eax',
        code=binascii.unhexlify('85c0'),
        initial_regs={'EAX': 0xFFFFFFFF},
        initial_eflags=0,
        expected_eflags=0x80 | 0x4 # SF=1, PF=1 (0xFF has 8 bits -> even -> PF=1)
    )

@pytest.mark.regression
def test_id_91_test_r8_imm8():
    """Test: ID_91: test al, 1"""
    runner = Runner()
    # Raw: a801
    assert runner.run_test_bytes(
        name='ID_91: test al, 1',
        code=binascii.unhexlify('a801'),
        initial_regs={'EAX': 0x01},
        initial_eflags=0,
        expected_eflags=0 # PF=0 (Result 1 -> odd -> PF=0)
    )

@pytest.mark.regression
def test_id_64_test_r8_r8():
    """Test: ID_64: test dl, dl"""
    runner = Runner()
    # Raw: 84d2
    assert runner.run_test_bytes(
        name='ID_64: test dl, dl',
        code=binascii.unhexlify('84d2'),
        initial_regs={'EDX': 0},
        initial_eflags=0,
        expected_eflags=0x40 | 0x4 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_436_tzcnt_r32_r32():
    """Test: ID_436: tzcnt ecx, esi"""
    runner = Runner()
    # Raw: f30fbcce
    assert runner.run_test_bytes(
        name='ID_436: tzcnt ecx, esi',
        code=binascii.unhexlify('f30fbcce'),
        initial_regs={'ESI': 0x00000010},
        expected_regs={'ECX': 4},
        initial_eflags=0,
        expected_eflags=0 # ZF=0 (source != 0), CF=0 (source != 0)
    )

@pytest.mark.regression
def test_id_343_ucomisd_r128_m64():
    """Test: ID_343: ucomisd xmm0, qword ptr [ebp - 0x20]"""
    runner = Runner()
    # Raw: 660f2e45e0
    val_xmm0 = binascii.unhexlify('00000000000014400000000000000000') # 5.0
    val_mem = binascii.unhexlify('00000000000000400000000000000000') # 2.0
    assert runner.run_test_bytes(
        name='ID_343: ucomisd xmm0, qword ptr [ebp - 0x20]',
        code=binascii.unhexlify('660f2e45e0'),
        initial_regs={'EBP': 0x8000, 'XMM0': val_xmm0},
        expected_read={0x7FE0: int.from_bytes(val_mem, 'little')},
        initial_eflags=0x45, # ZF=1, PF=1, CF=1 (reset them)
        expected_eflags=0 # ZF=0, PF=0, CF=0 (5.0 > 2.0)
    )

@pytest.mark.regression
def test_id_326_ucomisd_r128_r128():
    """Test: ID_326: ucomisd xmm1, xmm0"""
    runner = Runner()
    # Raw: 660f2ec8
    val_xmm1 = binascii.unhexlify('00000000000000400000000000000000') # 2.0
    val_xmm0 = binascii.unhexlify('00000000000014400000000000000000') # 5.0
    assert runner.run_test_bytes(
        name='ID_326: ucomisd xmm1, xmm0',
        code=binascii.unhexlify('660f2ec8'),
        initial_regs={'XMM1': val_xmm1, 'XMM0': val_xmm0},
        initial_eflags=0,
        expected_eflags=0x01 # CF=1 (2.0 < 5.0)
    )

@pytest.mark.regression
def test_id_460_ucomiss_r128_m32():
    """Test: ID_460: ucomiss xmm0, dword ptr [ebp - 0x20]"""
    runner = Runner()
    # Raw: 0f2e45e0
    val_xmm0 = binascii.unhexlify('0000a040000000000000000000000000') # 5.0f
    val_mem = binascii.unhexlify('0000a040') # 5.0f
    assert runner.run_test_bytes(
        name='ID_460: ucomiss xmm0, dword ptr [ebp - 0x20]',
        code=binascii.unhexlify('0f2e45e0'),
        initial_regs={'EBP': 0x8000, 'XMM0': val_xmm0},
        expected_read={0x7FE0: int.from_bytes(val_mem, 'little')},
        initial_eflags=0,
        expected_eflags=0x40 # ZF=1 (5.0 == 5.0)
    )

@pytest.mark.regression
def test_id_459_ucomiss_r128_r128():
    """Test: ID_459: ucomiss xmm0, xmm1"""
    runner = Runner()
    # Raw: 0f2ec1
    val_xmm0 = binascii.unhexlify('0000803f000000000000000000000000') # 1.0f
    val_xmm1 = binascii.unhexlify('00000000000000000000000000000000') # 0.0f
    assert runner.run_test_bytes(
        name='ID_459: ucomiss xmm0, xmm1',
        code=binascii.unhexlify('0f2ec1'),
        initial_regs={'XMM0': val_xmm0, 'XMM1': val_xmm1},
        initial_eflags=0x45,
        expected_eflags=0 # ZF=0, PF=0, CF=0 (1.0 > 0.0)
    )

@pytest.mark.regression
def test_id_386_unpckhpd_r128_r128():
    """Test: ID_386: unpckhpd xmm4, xmm5"""
    runner = Runner()
    # Raw: 660f15e5
    val_xmm4 = binascii.unhexlify('00112233445566778899aabbccddeeff')
    val_xmm5 = binascii.unhexlify('ffeeddccbbaa99887766554433221100')
    # DEST[63:0]   <- DEST[127:64] (0x8899aabbccddeeff)
    # DEST[127:64] <- SRC[127:64]  (0x0011223344556677 from Val5's high quad)
    # Note: Val5 bytes 8-15 are '7766554433221100'.
    # FIX: Expected high quad was incorrectly set to Src Low quad in original test.
    expected_val = binascii.unhexlify('8899aabbccddeeff7766554433221100')
    assert runner.run_test_bytes(
        name='ID_386: unpckhpd xmm4, xmm5',
        code=binascii.unhexlify('660f15e5'),
        initial_regs={'XMM4': val_xmm4, 'XMM5': val_xmm5},
        expected_regs={'XMM4': expected_val}
    )

@pytest.mark.regression
def test_id_493_unpcklpd_r128_m128():
    """Test: ID_493: unpcklpd xmm0, xmmword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 660f1445b8
    val_xmm0 = binascii.unhexlify('00112233445566778899aabbccddeeff')
    val_mem = binascii.unhexlify('ffeeddccbbaa99887766554433221100')
    # DEST[63:0]   <- DEST[63:0] (0x0011223344556677)
    # DEST[127:64] <- SRC[63:0]  (0x8899aabbccddeeff from ValMem's low quad)
    # ValMem Low Quad (bytes 0-7) is 'ffeeddccbbaa9988'.
    # FIX: Expected high quad was incorrectly set to Src High quad in original test.
    expected_val = binascii.unhexlify('0011223344556677ffeeddccbbaa9988')
    assert runner.run_test_bytes(
        name='ID_493: unpcklpd xmm0, xmmword ptr [ebp - 0x48]',
        code=binascii.unhexlify('660f1445b8'),
        initial_regs={'EBP': 0x8000, 'XMM0': val_xmm0},
        expected_read={0x7FB8: int.from_bytes(val_mem, 'little')},
        expected_regs={'XMM0': expected_val}
    )

@pytest.mark.regression
def test_id_388_unpcklpd_r128_r128():
    """Test: ID_388: unpcklpd xmm4, xmm4"""
    runner = Runner()
    # Raw: 660f14e4
    val_xmm4 = binascii.unhexlify('00112233445566778899aabbccddeeff')
    # DEST[63:0]   <- DEST[63:0] (0x0011223344556677)
    # DEST[127:64] <- SRC[63:0]  (0x0011223344556677)
    expected_val = binascii.unhexlify('00112233445566770011223344556677')
    assert runner.run_test_bytes(
        name='ID_388: unpcklpd xmm4, xmm4',
        code=binascii.unhexlify('660f14e4'),
        initial_regs={'XMM4': val_xmm4},
        expected_regs={'XMM4': expected_val}
    )

@pytest.mark.regression
def test_id_463_unpcklps_r128_m128():
    """Test: ID_463: unpcklps xmm0, xmmword ptr [ebp - 0xd8]"""
    runner = Runner()
    # Raw: 0f148528ffffff
    val_xmm0 = binascii.unhexlify('00000001000000020000000300000004')
    val_mem = binascii.unhexlify('00000005000000060000000700000008')
    # Interleave low single-precision:
    # DEST[31:0]   <- DEST[31:0] (1)
    # DEST[63:32]  <- SRC[31:0]  (5)
    # DEST[95:64]  <- DEST[63:32] (2)
    # DEST[127:96] <- SRC[63:32]  (6)
    expected_val = binascii.unhexlify('00000001000000050000000200000006')
    assert runner.run_test_bytes(
        name='ID_463: unpcklps xmm0, xmmword ptr [ebp - 0xd8]',
        code=binascii.unhexlify('0f148528ffffff'),
        initial_regs={'EBP': 0x8000, 'XMM0': val_xmm0},
        expected_read={0x7F28: int.from_bytes(val_mem, 'little')},
        expected_regs={'XMM0': expected_val}
    )

@pytest.mark.regression
def test_id_410_unpcklps_r128_r128():
    """Test: ID_410: unpcklps xmm0, xmm1"""
    runner = Runner()
    # Raw: 0f14c1
    val_xmm0 = binascii.unhexlify('00000001000000020000000300000004')
    val_xmm1 = binascii.unhexlify('00000005000000060000000700000008')
    expected_val = binascii.unhexlify('00000001000000050000000200000006')
    assert runner.run_test_bytes(
        name='ID_410: unpcklps xmm0, xmm1',
        code=binascii.unhexlify('0f14c1'),
        initial_regs={'XMM0': val_xmm0, 'XMM1': val_xmm1},
        expected_regs={'XMM0': expected_val}
    )

@pytest.mark.regression
def test_id_406_xchg_m32_r32():
    """Test: ID_406: xchg dword ptr [ebx + 0x44], eax"""
    runner = Runner()
    # Raw: 878344000000
    assert runner.run_test_bytes(
        name='ID_406: xchg dword ptr [ebx + 0x44], eax',
        code=binascii.unhexlify('878344000000'),
        initial_regs={'EBX': 0x2000, 'EAX': 0x12345678},
        expected_read={0x2044: 0xDEADBEEF},
        expected_write={0x2044: 0x12345678},
        expected_regs={'EAX': 0xDEADBEEF}
    )

@pytest.mark.regression
def test_id_372_xor_m32_imm32():
    """Test: ID_372: xor dword ptr [ebp - 0x20], 0x6e646f6d"""
    runner = Runner()
    # Raw: 8175e06d6f646e
    assert runner.run_test_bytes(
        name='ID_372: xor dword ptr [ebp - 0x20], 0x6e646f6d',
        code=binascii.unhexlify('8175e06d6f646e'),
        initial_regs={'EBP': 0x8000},
        expected_read={0x7FE0: 0x00000000},
        expected_write={0x7FE0: 0x6e646f6d},
        initial_eflags=0,
        # FIX: Low byte 0x6d (01101101) has 5 bits set -> Odd Parity -> PF=0.
        expected_eflags=0 
    )