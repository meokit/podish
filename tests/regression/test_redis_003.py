# Redis Regression Test Batch 003
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_70_ja_imm32():
    """Test: ID_70: ja 0x103f"""
    runner = Runner()
    # Raw: 773d
    assert runner.run_test_bytes(
        name='ID_70: ja 0x103f',
        code=binascii.unhexlify('773d'),
        initial_regs={},
        initial_eflags=0, # CF=0, ZF=0 (JA condition met)
        expected_eip=0x103f # 0x1002 + 0x3d
    )

@pytest.mark.regression
def test_id_113_jae_imm32():
    """Test: ID_113: jae 0x1401"""
    runner = Runner()
    # Raw: 0f83fb030000
    assert runner.run_test_bytes(
        name='ID_113: jae 0x1401',
        code=binascii.unhexlify('0f83fb030000'),
        initial_regs={},
        initial_eflags=0, # CF=0 (JAE condition met)
        expected_eip=0x1401 # 0x1006 + 0x3fb
    )

@pytest.mark.regression
def test_id_76_jb_imm32():
    """Test: ID_76: jb 0x104e"""
    runner = Runner()
    # Raw: 724c
    assert runner.run_test_bytes(
        name='ID_76: jb 0x104e',
        code=binascii.unhexlify('724c'),
        initial_regs={},
        initial_eflags=1, # CF=1 (JB condition met)
        expected_eip=0x104e # 0x1002 + 0x4c
    )

@pytest.mark.regression
def test_id_181_jbe_imm32():
    """Test: ID_181: jbe 0x1231"""
    runner = Runner()
    # Raw: 0f862b020000
    assert runner.run_test_bytes(
        name='ID_181: jbe 0x1231',
        code=binascii.unhexlify('0f862b020000'),
        initial_regs={},
        initial_eflags=1, # CF=1 (JBE condition met)
        expected_eip=0x1231 # 0x1006 + 0x22b
    )

@pytest.mark.regression
def test_id_14_je_imm32():
    """Test: ID_14: je 0x1179"""
    runner = Runner()
    # Raw: 0f8473010000
    assert runner.run_test_bytes(
        name='ID_14: je 0x1179',
        code=binascii.unhexlify('0f8473010000'),
        initial_regs={},
        initial_eflags=0x40, # ZF=1 (JE condition met)
        expected_eip=0x1179 # 0x1006 + 0x173
    )

@pytest.mark.regression
def test_id_20_jg_imm32():
    """Test: ID_20: jg 0x1093"""
    runner = Runner()
    # Raw: 0f8f8d000000
    assert runner.run_test_bytes(
        name='ID_20: jg 0x1093',
        code=binascii.unhexlify('0f8f8d000000'),
        initial_regs={},
        initial_eflags=0, # ZF=0, SF=0, OF=0 (JG condition met)
        expected_eip=0x1093
    )

@pytest.mark.regression
def test_id_94_jge_imm32():
    """Test: ID_94: jge 0xf13"""
    runner = Runner()
    # Raw: 0f8d0dffffff
    assert runner.run_test_bytes(
        name='ID_94: jge 0xf13',
        code=binascii.unhexlify('0f8d0dffffff'),
        initial_regs={},
        initial_eflags=0, # SF=0, OF=0 (JGE condition met)
        expected_eip=0xf13
    )

@pytest.mark.regression
def test_id_52_jl_imm32():
    """Test: ID_52: jl 0xfe7"""
    runner = Runner()
    # Raw: 7ce5
    assert runner.run_test_bytes(
        name='ID_52: jl 0xfe7',
        code=binascii.unhexlify('7ce5'),
        initial_regs={},
        initial_eflags=0x80, # SF=1, OF=0 (JL condition met)
        expected_eip=0xfe7
    )

@pytest.mark.regression
def test_id_50_jle_imm32():
    """Test: ID_50: jle 0x1027"""
    runner = Runner()
    # Raw: 7e25
    assert runner.run_test_bytes(
        name='ID_50: jle 0x1027',
        code=binascii.unhexlify('7e25'),
        initial_regs={},
        initial_eflags=0x40, # ZF=1 (JLE condition met)
        expected_eip=0x1027
    )

@pytest.mark.regression
def test_id_22_jmp_imm32():
    """Test: ID_22: jmp 0x1073"""
    runner = Runner()
    # Raw: eb71
    assert runner.run_test_bytes(
        name='ID_22: jmp 0x1073',
        code=binascii.unhexlify('eb71'),
        initial_regs={},
        expected_eip=0x1073
    )

@pytest.mark.regression
def test_id_71_jmp_r32():
    """Test: ID_71: jmp edx"""
    runner = Runner()
    # Raw: ffe2
    assert runner.run_test_bytes(
        name='ID_71: jmp edx',
        code=binascii.unhexlify('ffe2'),
        initial_regs={'EDX': 0x1005},
        expected_eip=0x1005
    )

@pytest.mark.regression
def test_id_18_jne_imm32():
    """Test: ID_18: jne 0x1069"""
    runner = Runner()
    # Raw: 7567
    assert runner.run_test_bytes(
        name='ID_18: jne 0x1069',
        code=binascii.unhexlify('7567'),
        initial_regs={},
        initial_eflags=0, # ZF=0 (JNE condition met)
        expected_eip=0x1069 # 0x1002 + 0x67
    )

@pytest.mark.regression
def test_id_368_jno_imm32():
    """Test: ID_368: jno 0x100b"""
    runner = Runner()
    # Raw: 7109
    assert runner.run_test_bytes(
        name='ID_368: jno 0x100b',
        code=binascii.unhexlify('7109'),
        initial_regs={},
        initial_eflags=0, # OF=0 (JNO condition met)
        expected_eip=0x100b
    )

@pytest.mark.regression
def test_id_351_jnp_imm32():
    """Test: ID_351: jnp 0x12f6"""
    runner = Runner()
    # Raw: 0f8bf0020000
    assert runner.run_test_bytes(
        name='ID_351: jnp 0x12f6',
        code=binascii.unhexlify('0f8bf0020000'),
        initial_regs={},
        initial_eflags=0, # PF=0 (JNP condition met)
        expected_eip=0x12f6
    )

@pytest.mark.regression
def test_id_227_jns_imm32():
    """Test: ID_227: jns 0xde6"""
    runner = Runner()
    # Raw: 0f89e0fdffff
    assert runner.run_test_bytes(
        name='ID_227: jns 0xde6',
        code=binascii.unhexlify('0f89e0fdffff'),
        initial_regs={},
        initial_eflags=0, # SF=0 (JNS condition met)
        expected_eip=0xde6
    )

@pytest.mark.regression
def test_id_342_jo_imm32():
    """Test: ID_342: jo 0xeb0"""
    runner = Runner()
    # Raw: 0f80aafeffff
    assert runner.run_test_bytes(
        name='ID_342: jo 0xeb0',
        code=binascii.unhexlify('0f80aafeffff'),
        initial_regs={},
        initial_eflags=0x800, # OF=1 (JO condition met)
        expected_eip=0xeb0
    )

@pytest.mark.regression
def test_id_346_jp_imm32():
    """Test: ID_346: jp 0x12b9"""
    runner = Runner()
    # Raw: 0f8ab3020000
    assert runner.run_test_bytes(
        name='ID_346: jp 0x12b9',
        code=binascii.unhexlify('0f8ab3020000'),
        initial_regs={},
        initial_eflags=0x4, # PF=1 (JP condition met)
        expected_eip=0x12b9
    )

@pytest.mark.regression
def test_id_125_js_imm32():
    """Test: ID_125: js 0x10a6"""
    runner = Runner()
    # Raw: 0f88a0000000
    assert runner.run_test_bytes(
        name='ID_125: js 0x10a6',
        code=binascii.unhexlify('0f88a0000000'),
        initial_regs={},
        initial_eflags=0x80, # SF=1 (JS condition met)
        expected_eip=0x10a6
    )

@pytest.mark.regression
def test_id_21_lea_r32_m32():
    """Test: ID_21: lea eax, [ebx]"""
    runner = Runner()
    # Raw: 8d8300000000
    assert runner.run_test_bytes(
        name='ID_21: lea eax, [ebx]',
        code=binascii.unhexlify('8d8300000000'),
        initial_regs={'EBX': 0x2000},
        expected_regs={'EAX': 0x2000}
    )

@pytest.mark.regression
def test_id_367_lock_add_m32_r32():
    """Test: ID_367: lock add dword ptr [ebx], eax"""
    runner = Runner()
    # Raw: f0018300000000
    assert runner.run_test_bytes(
        name='ID_367: lock add dword ptr [ebx], eax',
        code=binascii.unhexlify('f0018300000000'),
        initial_regs={'EBX': 0x2000, 'EAX': 0x1},
        expected_read={0x2000: 0}, # Mem init 0 (default)
        expected_write={0x2000: 1}, # 0 + 1 = 1
        expected_regs={'EAX': 0x1},
        initial_eflags=0,
        expected_eflags=0 # ZF=0 (Result 1 != 0)
    )

@pytest.mark.regression
def test_id_454_lock_cmpxchg8b_m64():
    """Test: ID_454: lock cmpxchg8b qword ptr [esi + 0x748]"""
    runner = Runner()
    # Raw: f00fc78e48070000
    assert runner.run_test_bytes(
        name='ID_454: lock cmpxchg8b qword ptr [esi + 0x748]',
        code=binascii.unhexlify('f00fc78e48070000'),
        initial_regs={'ESI': 0x2000, 'EAX': 0, 'EDX': 0, 'EBX': 2, 'ECX': 0},
        expected_read={0x2748: 0},
        expected_write={0x2748: 2},
        initial_eflags=0,
        expected_eflags=0x40 # ZF=1 (Match)
    )

@pytest.mark.regression
def test_id_412_lock_dec_m32():
    """Test: ID_412: lock dec dword ptr [ebx + 0x44]"""
    runner = Runner()
    # Raw: f0ff8b44000000
    assert runner.run_test_bytes(
        name='ID_412: lock dec dword ptr [ebx + 0x44]',
        code=binascii.unhexlify('f0ff8b44000000'),
        initial_regs={'EBX': 0x2000},
        expected_read={0x2044: 1},
        expected_write={0x2044: 0},
        initial_eflags=0,
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_407_lock_inc_m32():
    """Test: ID_407: lock inc dword ptr [ebx + 0x44]"""
    runner = Runner()
    # Raw: f0ff8344000000
    assert runner.run_test_bytes(
        name='ID_407: lock inc dword ptr [ebx + 0x44]',
        code=binascii.unhexlify('f0ff8344000000'),
        initial_regs={'EBX': 0x2000},
        expected_read={0x2044: 0},
        expected_write={0x2044: 1},
        initial_eflags=0x40, # ZF=1
        expected_eflags=0 # ZF=0
    )

@pytest.mark.regression
def test_id_488_lock_or_m32_imm32():
    """Test: ID_488: lock or dword ptr [esp], 0"""
    runner = Runner()
    # Raw: f0830c2400
    assert runner.run_test_bytes(
        name='ID_488: lock or dword ptr [esp], 0',
        code=binascii.unhexlify('f0830c2400'),
        initial_regs={'ESP': 0x2000},
        expected_read={0x2000: 0x5A5A5A5A},
        expected_write={0x2000: 0x5A5A5A5A}, # No change
        initial_eflags=0,
        expected_eflags=0x4 # PF=1
    )

@pytest.mark.regression
def test_id_369_lock_sub_m32_r32():
    """Test: ID_369: lock sub dword ptr [ebx], eax"""
    runner = Runner()
    # Raw: f0298300000000
    assert runner.run_test_bytes(
        name='ID_369: lock sub dword ptr [ebx], eax',
        code=binascii.unhexlify('f0298300000000'),
        initial_regs={'EBX': 0x2000, 'EAX': 1},
        expected_read={0x2000: 1},
        expected_write={0x2000: 0},
        initial_eflags=0,
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_411_lock_xadd_m32_r32():
    """Test: ID_411: lock xadd dword ptr [ecx + 0x4c], eax"""
    runner = Runner()
    # Raw: f00fc1814c000000
    assert runner.run_test_bytes(
        name='ID_411: lock xadd dword ptr [ecx + 0x4c], eax',
        code=binascii.unhexlify('f00fc1814c000000'),
        initial_regs={'ECX': 0x2000, 'EAX': 2},
        expected_read={0x204c: 1},
        expected_write={0x204c: 3},
        expected_regs={'EAX': 1},
        initial_eflags=0,
        expected_eflags=0x4 # PF=1 (Result 3 has even parity)
    )

@pytest.mark.regression
def test_id_398_maxpd_r128_r128():
    """Test: ID_398: maxpd xmm3, xmm1"""
    runner = Runner()
    # Raw: 660f5fd9
    assert runner.run_test_bytes(
        name='ID_398: maxpd xmm3, xmm1',
        code=binascii.unhexlify('660f5fd9'),
        initial_regs={
            'XMM1': binascii.unhexlify('00000000000000400000000000001040'), # 2.0, 4.0
            'XMM3': binascii.unhexlify('000000000000f03f0000000000001440')  # 1.0, 5.0
        },
        expected_regs={
            'XMM3': binascii.unhexlify('00000000000000400000000000001440')  # 2.0, 5.0
        }
    )

@pytest.mark.regression
def test_id_358_maxsd_r128_r128():
    """Test: ID_358: maxsd xmm1, xmm0"""
    runner = Runner()
    # Raw: f20f5fc8
    assert runner.run_test_bytes(
        name='ID_358: maxsd xmm1, xmm0',
        code=binascii.unhexlify('f20f5fc8'),
        initial_regs={
            'XMM0': binascii.unhexlify('00000000000000400000000000000000'), # 2.0, 0.0
            'XMM1': binascii.unhexlify('000000000000f03f0000000000000000')  # 1.0, 0.0
        },
        expected_regs={
            'XMM1': binascii.unhexlify('00000000000000400000000000000000')  # 2.0, 0.0
        }
    )

@pytest.mark.regression
def test_id_397_minpd_r128_r128():
    """Test: ID_397: minpd xmm1, xmm0"""
    runner = Runner()
    # Raw: 660f5dc8
    assert runner.run_test_bytes(
        name='ID_397: minpd xmm1, xmm0',
        code=binascii.unhexlify('660f5dc8'),
        initial_regs={
            'XMM0': binascii.unhexlify('00000000000000400000000000001040'), # 2.0, 4.0
            'XMM1': binascii.unhexlify('000000000000f03f0000000000001440')  # 1.0, 5.0
        },
        expected_regs={
            'XMM1': binascii.unhexlify('000000000000f03f0000000000001040')  # 1.0, 4.0
        }
    )

@pytest.mark.regression
def test_id_354_minsd_r128_r128():
    """Test: ID_354: minsd xmm1, xmm0"""
    runner = Runner()
    # Raw: f20f5dc8
    assert runner.run_test_bytes(
        name='ID_354: minsd xmm1, xmm0',
        code=binascii.unhexlify('f20f5dc8'),
        initial_regs={
            'XMM0': binascii.unhexlify('00000000000000400000000000000000'), # 2.0, 0.0
            'XMM1': binascii.unhexlify('000000000000f03f0000000000000000')  # 1.0, 0.0
        },
        expected_regs={
            'XMM1': binascii.unhexlify('000000000000f03f0000000000000000')  # 1.0, 0.0
        }
    )

@pytest.mark.regression
def test_id_173_mov_m16_imm16():
    """Test: ID_173: mov word ptr [edi + 4], 0"""
    runner = Runner()
    # Raw: 66c747040000
    assert runner.run_test_bytes(
        name='ID_173: mov word ptr [edi + 4], 0',
        code=binascii.unhexlify('66c747040000'),
        initial_regs={'EDI': 0x2000},
        expected_write={0x2004: 0x0000}
    )

@pytest.mark.regression
def test_id_156_mov_m16_r16():
    """Test: ID_156: mov word ptr [ebp - 0xc8], cx"""
    runner = Runner()
    # Raw: 66898d38ffffff
    assert runner.run_test_bytes(
        name='ID_156: mov word ptr [ebp - 0xc8], cx',
        code=binascii.unhexlify('66898d38ffffff'),
        initial_regs={'EBP': 0x8000, 'ECX': 0x1234},
        expected_write={0x8000 - 0xc8: 0x1234}
    )

@pytest.mark.regression
def test_id_11_mov_m32_imm32():
    """Test: ID_11: mov dword ptr [esp], 0"""
    runner = Runner()
    # Raw: c7042400000000
    assert runner.run_test_bytes(
        name='ID_11: mov dword ptr [esp], 0',
        code=binascii.unhexlify('c7042400000000'),
        initial_regs={'ESP': 0x7FFC},
        expected_write={0x7FFC: 0}
    )

@pytest.mark.regression
def test_id_15_mov_m32_r32():
    """Test: ID_15: mov dword ptr [esp + 4], eax"""
    runner = Runner()
    # Raw: 89442404
    assert runner.run_test_bytes(
        name='ID_15: mov dword ptr [esp + 4], eax',
        code=binascii.unhexlify('89442404'),
        initial_regs={'ESP': 0x7FFC, 'EAX': 0xDEADBEEF},
        expected_write={0x8000: 0xDEADBEEF}
    )

@pytest.mark.regression
def test_id_38_mov_m8_imm8():
    """Test: ID_38: mov byte ptr [ebx + 0x28], 1"""
    runner = Runner()
    # Raw: c6832800000001
    assert runner.run_test_bytes(
        name='ID_38: mov byte ptr [ebx + 0x28], 1',
        code=binascii.unhexlify('c6832800000001'),
        initial_regs={'EBX': 0x2000},
        expected_write={0x2028: 1}
    )

@pytest.mark.regression
def test_id_111_mov_m8_r8():
    """Test: ID_111: mov byte ptr [ebp - 0x14], al"""
    runner = Runner()
    # Raw: 8845ec
    assert runner.run_test_bytes(
        name='ID_111: mov byte ptr [ebp - 0x14], al',
        code=binascii.unhexlify('8845ec'),
        initial_regs={'EBP': 0x8000, 'EAX': 0x77},
        expected_write={0x8000 - 0x14: 0x77}
    )

@pytest.mark.regression
def test_id_155_mov_r16_imm16():
    """Test: ID_155: mov cx, 2"""
    runner = Runner()
    # Raw: 66b90200
    assert runner.run_test_bytes(
        name='ID_155: mov cx, 2',
        code=binascii.unhexlify('66b90200'),
        initial_regs={'ECX': 0},
        expected_regs={'ECX': 2}
    )

@pytest.mark.regression
def test_id_492_mov_r16_m16():
    """Test: ID_492: mov cx, word ptr [esi]"""
    runner = Runner()
    # Raw: 668b0e
    assert runner.run_test_bytes(
        name='ID_492: mov cx, word ptr [esi]',
        code=binascii.unhexlify('668b0e'),
        initial_regs={'ESI': 0x2000},
        expected_read={0x2000: 0xABCD},
        expected_regs={'ECX': 0xABCD}
    )

@pytest.mark.regression
def test_id_17_mov_r32_imm32():
    """Test: ID_17: mov edi, 8"""
    runner = Runner()
    # Raw: bf08000000
    assert runner.run_test_bytes(
        name='ID_17: mov edi, 8',
        code=binascii.unhexlify('bf08000000'),
        initial_regs={'EDI': 0},
        expected_regs={'EDI': 8}
    )

@pytest.mark.regression
def test_id_10_mov_r32_m32():
    """Test: ID_10: mov eax, dword ptr [ebp + 8]"""
    runner = Runner()
    # Raw: 8b4508
    assert runner.run_test_bytes(
        name='ID_10: mov eax, dword ptr [ebp + 8]',
        code=binascii.unhexlify('8b4508'),
        initial_regs={'EBP': 0x8000},
        expected_read={0x8008: 0x12345678},
        expected_regs={'EAX': 0x12345678}
    )

@pytest.mark.regression
def test_id_2_mov_r32_r32():
    """Test: ID_2: mov ebp, esp"""
    runner = Runner()
    # Raw: 89e5
    assert runner.run_test_bytes(
        name='ID_2: mov ebp, esp',
        code=binascii.unhexlify('89e5'),
        initial_regs={'ESP': 0x8000},
        expected_regs={'EBP': 0x8000}
    )

@pytest.mark.regression
def test_id_98_mov_r8_imm8():
    """Test: ID_98: mov al, 1"""
    runner = Runner()
    # Raw: b001
    assert runner.run_test_bytes(
        name='ID_98: mov al, 1',
        code=binascii.unhexlify('b001'),
        initial_regs={'EAX': 0},
        expected_regs={'EAX': 1}
    )

@pytest.mark.regression
def test_id_102_mov_r8_m8():
    """Test: ID_102: mov al, byte ptr [ebp - 0x38]"""
    runner = Runner()
    # Raw: 8a45c8
    assert runner.run_test_bytes(
        name='ID_102: mov al, byte ptr [ebp - 0x38]',
        code=binascii.unhexlify('8a45c8'),
        initial_regs={'EBP': 0x8000},
        expected_read={0x8000 - 0x38: 0xCC},
        expected_regs={'EAX': 0xCC}
    )

@pytest.mark.regression
def test_id_59_mov_r8_r8():
    """Test: ID_59: mov dh, dl"""
    runner = Runner()
    # Raw: 88d6
    assert runner.run_test_bytes(
        name='ID_59: mov dh, dl',
        code=binascii.unhexlify('88d6'),
        initial_regs={'EDX': 0x1234}, # DL = 0x34
        expected_regs={'EDX': 0x3434}  # DH = 0x34, DL = 0x34
    )

@pytest.mark.regression
def test_id_245_movapd_m128_r128():
    """Test: ID_245: movapd xmmword ptr [ebp - 0xfe8], xmm0"""
    runner = Runner()
    # Raw: 660f298518f0ffff
    val = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_245: movapd xmmword ptr [ebp - 0xfe8], xmm0',
        code=binascii.unhexlify('660f298518f0ffff'),
        initial_regs={'EBP': 0x4000, 'XMM0': val},
        expected_write={0x4000 - 0xfe8: int.from_bytes(val, 'little')}
    )

@pytest.mark.regression
def test_id_325_movapd_r128_m128():
    """Test: ID_325: movapd xmm3, xmmword ptr [ebp - 0x78]"""
    runner = Runner()
    # Raw: 660f285d88
    val = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_325: movapd xmm3, xmmword ptr [ebp - 0x78]',
        code=binascii.unhexlify('660f285d88'),
        initial_regs={'EBP': 0x4000},
        expected_read={0x4000 - 0x78: int.from_bytes(val, 'little')},
        expected_regs={'XMM3': val}
    )

@pytest.mark.regression
def test_id_355_movapd_r128_r128():
    """Test: ID_355: movapd xmm0, xmm1"""
    runner = Runner()
    # Raw: 660f28c1
    val = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_355: movapd xmm0, xmm1',
        code=binascii.unhexlify('660f28c1'),
        initial_regs={'XMM1': val},
        expected_regs={'XMM0': val}
    )

@pytest.mark.regression
def test_id_157_movaps_m128_r128():
    """Test: ID_157: movaps xmmword ptr [ebp - 0x38], xmm0"""
    runner = Runner()
    # Raw: 0f2945c8
    val = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_157: movaps xmmword ptr [ebp - 0x38], xmm0',
        code=binascii.unhexlify('0f2945c8'),
        initial_regs={'EBP': 0x4000, 'XMM0': val},
        expected_write={0x4000 - 0x38: int.from_bytes(val, 'little')}
    )

@pytest.mark.regression
def test_id_241_movaps_r128_m128():
    """Test: ID_241: movaps xmm0, xmmword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 0f2845b8
    val = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_241: movaps xmm0, xmmword ptr [ebp - 0x48]',
        code=binascii.unhexlify('0f2845b8'),
        initial_regs={'EBP': 0x4000},
        expected_read={0x4000 - 0x48: int.from_bytes(val, 'little')},
        expected_regs={'XMM0': val}
    )

@pytest.mark.regression
def test_id_277_movaps_r128_r128():
    """Test: ID_277: movaps xmm2, xmm6"""
    runner = Runner()
    # Raw: 0f28d6
    val = binascii.unhexlify('0123456789ABCDEF0123456789ABCDEF')
    assert runner.run_test_bytes(
        name='ID_277: movaps xmm2, xmm6',
        code=binascii.unhexlify('0f28d6'),
        initial_regs={'XMM6': val},
        expected_regs={'XMM2': val}
    )

