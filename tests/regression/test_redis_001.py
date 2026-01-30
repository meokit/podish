# Redis Regression Test Batch 001
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_226_cmova_r32_r32():
    """Test: ID_226: cmova ecx, ebx"""
    runner = Runner()
    # Raw: 0f47cb
    # Above: CF=0 and ZF=0
    assert runner.run_test_bytes(
        name='ID_226: cmova ecx, ebx',
        code=binascii.unhexlify('0f47cb'),
        initial_regs={'ECX': 0x11111111, 'EBX': 0x22222222},
        expected_regs={'ECX': 0x22222222},
        initial_eflags=0x202 # CF=0, ZF=0
    )

@pytest.mark.regression
def test_id_150_cmovae_r32_r32():
    """Test: ID_150: cmovae edi, edx"""
    runner = Runner()
    # Raw: 0f43fa
    # Above or Equal: CF=0
    assert runner.run_test_bytes(
        name='ID_150: cmovae edi, edx',
        code=binascii.unhexlify('0f43fa'),
        initial_regs={'EDI': 0x11111111, 'EDX': 0x22222222},
        expected_regs={'EDI': 0x22222222},
        initial_eflags=0x202 # CF=0
    )

@pytest.mark.regression
def test_id_190_cmovb_r32_m32():
    """Test: ID_190: cmovb ecx, dword ptr [ebp - 0x18]"""
    runner = Runner()
    # Raw: 0f424de8
    # Below: CF=1
    # Address: 0x8000 - 0x18 = 0x7FE8
    assert runner.run_test_bytes(
        name='ID_190: cmovb ecx, dword ptr [ebp - 0x18]',
        code=binascii.unhexlify('0f424de8'),
        initial_regs={'ECX': 0x11111111, 'EBP': 0x8000},
        expected_regs={'ECX': 0xDEADBEEF},
        initial_eflags=0x203, # CF=1
        expected_read={0x7FE8: 0xDEADBEEF}
    )

@pytest.mark.regression
def test_id_116_cmovb_r32_r32():
    """Test: ID_116: cmovb edx, edi"""
    runner = Runner()
    # Raw: 0f42d7
    # Below: CF=1
    assert runner.run_test_bytes(
        name='ID_116: cmovb edx, edi',
        code=binascii.unhexlify('0f42d7'),
        initial_regs={'EDX': 0x11111111, 'EDI': 0x22222222},
        expected_regs={'EDX': 0x22222222},
        initial_eflags=0x203 # CF=1
    )

@pytest.mark.regression
def test_id_418_cmovbe_r32_r32():
    """Test: ID_418: cmovbe ebx, edi"""
    runner = Runner()
    # Raw: 0f46df
    # Below or Equal: CF=1 or ZF=1
    assert runner.run_test_bytes(
        name='ID_418: cmovbe ebx, edi',
        code=binascii.unhexlify('0f46df'),
        initial_regs={'EBX': 0x11111111, 'EDI': 0x22222222},
        expected_regs={'EBX': 0x22222222},
        initial_eflags=0x242 # ZF=1
    )

@pytest.mark.regression
def test_id_124_cmove_r32_m32():
    """Test: ID_124: cmove edx, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 0f4455f0
    # Equal: ZF=1
    # Address: 0x8000 - 0x10 = 0x7FF0
    assert runner.run_test_bytes(
        name='ID_124: cmove edx, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('0f4455f0'),
        initial_regs={'EDX': 0x11111111, 'EBP': 0x8000},
        expected_regs={'EDX': 0xCAFEBABE},
        initial_eflags=0x242, # ZF=1
        expected_read={0x7FF0: 0xCAFEBABE}
    )

@pytest.mark.regression
def test_id_123_cmove_r32_r32():
    """Test: ID_123: cmove esi, edx"""
    runner = Runner()
    # Raw: 0f44f2
    # Equal: ZF=1
    assert runner.run_test_bytes(
        name='ID_123: cmove esi, edx',
        code=binascii.unhexlify('0f44f2'),
        initial_regs={'ESI': 0x11111111, 'EDX': 0x22222222},
        expected_regs={'ESI': 0x22222222},
        initial_eflags=0x242 # ZF=1
    )

@pytest.mark.regression
def test_id_426_cmovg_r32_m32():
    """Test: ID_426: cmovg ebx, dword ptr [ebp - 0x1c]"""
    runner = Runner()
    # Raw: 0f4f5de4
    # Greater: ZF=0 and SF=OF
    # Address: 0x8000 - 0x1C = 0x7FE4
    assert runner.run_test_bytes(
        name='ID_426: cmovg ebx, dword ptr [ebp - 0x1c]',
        code=binascii.unhexlify('0f4f5de4'),
        initial_regs={'EBX': 0x11111111, 'EBP': 0x8000},
        expected_regs={'EBX': 0xBAADF00D},
        initial_eflags=0x202, # ZF=0, SF=0, OF=0
        expected_read={0x7FE4: 0xBAADF00D}
    )

@pytest.mark.regression
def test_id_212_cmovg_r32_r32():
    """Test: ID_212: cmovg eax, edx"""
    runner = Runner()
    # Raw: 0f4fc2
    # Greater: ZF=0 and SF=OF
    assert runner.run_test_bytes(
        name='ID_212: cmovg eax, edx',
        code=binascii.unhexlify('0f4fc2'),
        initial_regs={'EAX': 0x11111111, 'EDX': 0x22222222},
        expected_regs={'EAX': 0x22222222},
        initial_eflags=0x202 # ZF=0, SF=0, OF=0
    )

@pytest.mark.regression
def test_id_185_cmovge_r32_r32():
    """Test: ID_185: cmovge esi, edi"""
    runner = Runner()
    # Raw: 0f4df7
    # Greater or Equal: SF=OF
    assert runner.run_test_bytes(
        name='ID_185: cmovge esi, edi',
        code=binascii.unhexlify('0f4df7'),
        initial_regs={'ESI': 0x11111111, 'EDI': 0x22222222},
        expected_regs={'ESI': 0x22222222},
        initial_eflags=0x882 # SF=1, OF=1
    )

@pytest.mark.regression
def test_id_199_cmovl_r32_m32():
    """Test: ID_199: cmovl eax, dword ptr [ebp - 0x1c]"""
    runner = Runner()
    # Raw: 0f4c45e4
    # Less: SF != OF
    # Address: 0x8000 - 0x1C = 0x7FE4
    assert runner.run_test_bytes(
        name='ID_199: cmovl eax, dword ptr [ebp - 0x1c]',
        code=binascii.unhexlify('0f4c45e4'),
        initial_regs={'EAX': 0x11111111, 'EBP': 0x8000},
        expected_regs={'EAX': 0xDEADBEEF},
        initial_eflags=0x282, # SF=1, OF=0
        expected_read={0x7FE4: 0xDEADBEEF}
    )

@pytest.mark.regression
def test_id_196_cmovl_r32_r32():
    """Test: ID_196: cmovl esi, ecx"""
    runner = Runner()
    # Raw: 0f4cf1
    # Less: SF != OF
    assert runner.run_test_bytes(
        name='ID_196: cmovl esi, ecx',
        code=binascii.unhexlify('0f4cf1'),
        initial_regs={'ESI': 0x11111111, 'ECX': 0x22222222},
        expected_regs={'ESI': 0x22222222},
        initial_eflags=0x282 # SF=1, OF=0
    )

@pytest.mark.regression
def test_id_206_cmovle_r32_r32():
    """Test: ID_206: cmovle eax, edx"""
    runner = Runner()
    # Raw: 0f4ec2
    # Less or Equal: ZF=1 or SF != OF
    assert runner.run_test_bytes(
        name='ID_206: cmovle eax, edx',
        code=binascii.unhexlify('0f4ec2'),
        initial_regs={'EAX': 0x11111111, 'EDX': 0x22222222},
        expected_regs={'EAX': 0x22222222},
        initial_eflags=0x242 # ZF=1
    )

@pytest.mark.regression
def test_id_290_cmovne_r32_m32():
    """Test: ID_290: cmovne ebx, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 0f455df0
    # Not Equal: ZF=0
    # Address: 0x8000 - 0x10 = 0x7FF0
    assert runner.run_test_bytes(
        name='ID_290: cmovne ebx, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('0f455df0'),
        initial_regs={'EBX': 0x11111111, 'EBP': 0x8000},
        expected_regs={'EBX': 0xCAFEBABE},
        initial_eflags=0x202, # ZF=0
        expected_read={0x7FF0: 0xCAFEBABE}
    )

@pytest.mark.regression
def test_id_126_cmovne_r32_r32():
    """Test: ID_126: cmovne ecx, edx"""
    runner = Runner()
    # Raw: 0f45ca
    # Not Equal: ZF=0
    assert runner.run_test_bytes(
        name='ID_126: cmovne ecx, edx',
        code=binascii.unhexlify('0f45ca'),
        initial_regs={'ECX': 0x11111111, 'EDX': 0x22222222},
        expected_regs={'ECX': 0x22222222},
        initial_eflags=0x202 # ZF=0
    )

@pytest.mark.regression
def test_id_479_cmovns_r32_m32():
    """Test: ID_479: cmovns esi, dword ptr [ebp - 0x28]"""
    runner = Runner()
    # Raw: 0f4975d8
    # Not Sign: SF=0
    # Address: 0x8000 - 0x28 = 0x7FD8
    assert runner.run_test_bytes(
        name='ID_479: cmovns esi, dword ptr [ebp - 0x28]',
        code=binascii.unhexlify('0f4975d8'),
        initial_regs={'ESI': 0x11111111, 'EBP': 0x8000},
        expected_regs={'ESI': 0xDEADBEEF},
        initial_eflags=0x202, # SF=0
        expected_read={0x7FD8: 0xDEADBEEF}
    )

@pytest.mark.regression
def test_id_137_cmovns_r32_r32():
    """Test: ID_137: cmovns eax, esi"""
    runner = Runner()
    # Raw: 0f49c6
    # Not Sign: SF=0
    assert runner.run_test_bytes(
        name='ID_137: cmovns eax, esi',
        code=binascii.unhexlify('0f49c6'),
        initial_regs={'EAX': 0x11111111, 'ESI': 0x22222222},
        expected_regs={'EAX': 0x22222222},
        initial_eflags=0x202 # SF=0
    )

@pytest.mark.regression
def test_id_464_cmovo_r32_r32():
    """Test: ID_464: cmovo eax, ecx"""
    runner = Runner()
    # Raw: 0f40c1
    # Overflow: OF=1
    assert runner.run_test_bytes(
        name='ID_464: cmovo eax, ecx',
        code=binascii.unhexlify('0f40c1'),
        initial_regs={'EAX': 0x11111111, 'ECX': 0x22222222},
        expected_regs={'EAX': 0x22222222},
        initial_eflags=0xA02 # OF=1
    )

@pytest.mark.regression
def test_id_207_cmovs_r32_m32():
    """Test: ID_207: cmovs ecx, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 0f484df0
    # Sign: SF=1
    # Address: 0x8000 - 0x10 = 0x7FF0
    assert runner.run_test_bytes(
        name='ID_207: cmovs ecx, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('0f484df0'),
        initial_regs={'ECX': 0x11111111, 'EBP': 0x8000},
        expected_regs={'ECX': 0xCAFEBABE},
        initial_eflags=0x282, # SF=1
        expected_read={0x7FF0: 0xCAFEBABE}
    )

@pytest.mark.regression
def test_id_297_cmovs_r32_r32():
    """Test: ID_297: cmovs edx, eax"""
    runner = Runner()
    # Raw: 0f48d0
    # Sign: SF=1
    assert runner.run_test_bytes(
        name='ID_297: cmovs edx, eax',
        code=binascii.unhexlify('0f48d0'),
        initial_regs={'EDX': 0x11111111, 'EAX': 0x22222222},
        expected_regs={'EDX': 0x22222222},
        initial_eflags=0x282 # SF=1
    )

@pytest.mark.regression
def test_id_178_cmp_m16_imm16():
    """Test: ID_178: cmp word ptr [ebp - 0x28], 0xa"""
    runner = Runner()
    # Raw: 66837dd80a
    # 0x8000 - 0x28 = 0x7FD8
    assert runner.run_test_bytes(
        name='ID_178: cmp word ptr [ebp - 0x28], 0xa',
        code=binascii.unhexlify('66837dd80a'),
        initial_regs={'EBP': 0x8000},
        expected_regs={'EBP': 0x8000},
        expected_eflags=0x44, # ZF=1, PF=1
        expected_read={0x7FD8: 0xA}
    )

@pytest.mark.regression
def test_id_19_cmp_m32_imm32():
    """Test: ID_19: cmp dword ptr [eax + 0xc64], 3"""
    runner = Runner()
    # Raw: 83b8640c000003
    # 0x2000 + 0xC64 = 0x2C64
    assert runner.run_test_bytes(
        name='ID_19: cmp dword ptr [eax + 0xc64], 3',
        code=binascii.unhexlify('83b8640c000003'),
        initial_regs={'EAX': 0x2000},
        expected_regs={'EAX': 0x2000},
        expected_eflags=0x0,
        expected_read={0x2C64: 0x5}
    )

@pytest.mark.regression
def test_id_115_cmp_m32_r32():
    """Test: ID_115: cmp dword ptr [edx + 4], edi"""
    runner = Runner()
    # Raw: 397a04
    # 0x2000 + 4 = 0x2004
    assert runner.run_test_bytes(
        name='ID_115: cmp dword ptr [edx + 4], edi',
        code=binascii.unhexlify('397a04'),
        initial_regs={'EDX': 0x2000, 'EDI': 0x12345678},
        expected_regs={'EDX': 0x2000, 'EDI': 0x12345678},
        expected_eflags=0x44, # ZF=1, PF=1
        expected_read={0x2004: 0x12345678}
    )

@pytest.mark.regression
def test_id_37_cmp_m8_imm8():
    """Test: ID_37: cmp byte ptr [ebx + 0x28], 1"""
    runner = Runner()
    # Raw: 80bb2800000001
    # 0x2000 + 0x28 = 0x2028
    assert runner.run_test_bytes(
        name='ID_37: cmp byte ptr [ebx + 0x28], 1',
        code=binascii.unhexlify('80bb2800000001'),
        initial_regs={'EBX': 0x2000},
        expected_regs={'EBX': 0x2000},
        expected_eflags=0x95, # CF=1, SF=1, PF=1, AF=1
        expected_read={0x2028: 0x0}
    )

@pytest.mark.regression
def test_id_230_cmp_m8_r8():
    """Test: ID_230: cmp byte ptr [esi + 5], al"""
    runner = Runner()
    # Raw: 384605
    # 0x2000 + 5 = 0x2005
    assert runner.run_test_bytes(
        name='ID_230: cmp byte ptr [esi + 5], al',
        code=binascii.unhexlify('384605'),
        initial_regs={'ESI': 0x2000, 'EAX': 0x55},
        expected_regs={'ESI': 0x2000, 'EAX': 0x55},
        expected_eflags=0x44, # ZF=1, PF=1
        expected_read={0x2005: 0x55}
    )

@pytest.mark.regression
def test_id_175_cmp_r16_imm16():
    """Test: ID_175: cmp ax, 3"""
    runner = Runner()
    # Raw: 6683f803
    assert runner.run_test_bytes(
        name='ID_175: cmp ax, 3',
        code=binascii.unhexlify('6683f803'),
        initial_regs={'EAX': 0x3},
        expected_regs={'EAX': 0x3},
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_322_cmp_r16_m16():
    """Test: ID_322: cmp ax, word ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 663b45f0
    # 0x8000 - 0x10 = 0x7FF0
    assert runner.run_test_bytes(
        name='ID_322: cmp ax, word ptr [ebp - 0x10]',
        code=binascii.unhexlify('663b45f0'),
        initial_regs={'EAX': 0x1234, 'EBP': 0x8000},
        expected_regs={'EAX': 0x1234, 'EBP': 0x8000},
        expected_eflags=0x44, # ZF=1, PF=1
        expected_read={0x7FF0: 0x1234}
    )

@pytest.mark.regression
def test_id_215_cmp_r16_r16():
    """Test: ID_215: cmp cx, dx"""
    runner = Runner()
    # Raw: 6639d1
    assert runner.run_test_bytes(
        name='ID_215: cmp cx, dx',
        code=binascii.unhexlify('6639d1'),
        initial_regs={'ECX': 0x100, 'EDX': 0x100},
        expected_regs={'ECX': 0x100, 'EDX': 0x100},
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_48_cmp_r32_imm32():
    """Test: ID_48: cmp esi, 8"""
    runner = Runner()
    # Raw: 83fe08
    assert runner.run_test_bytes(
        name='ID_48: cmp esi, 8',
        code=binascii.unhexlify('83fe08'),
        initial_regs={'ESI': 0x8},
        expected_regs={'ESI': 0x8},
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_51_cmp_r32_m32():
    """Test: ID_51: cmp esi, dword ptr [edi + 0x40]"""
    runner = Runner()
    # Raw: 3b7740
    # 0x2000 + 0x40 = 0x2040
    assert runner.run_test_bytes(
        name='ID_51: cmp esi, dword ptr [edi + 0x40]',
        code=binascii.unhexlify('3b7740'),
        initial_regs={'ESI': 0x12345678, 'EDI': 0x2000},
        expected_regs={'ESI': 0x12345678, 'EDI': 0x2000},
        expected_eflags=0x44, # ZF=1, PF=1
        expected_read={0x2040: 0x12345678}
    )

@pytest.mark.regression
def test_id_93_cmp_r32_r32():
    """Test: ID_93: cmp esi, eax"""
    runner = Runner()
    # Raw: 39c6
    assert runner.run_test_bytes(
        name='ID_93: cmp esi, eax',
        code=binascii.unhexlify('39c6'),
        initial_regs={'ESI': 0x100, 'EAX': 0x100},
        expected_regs={'ESI': 0x100, 'EAX': 0x100},
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_61_cmp_r8_imm8():
    """Test: ID_61: cmp dh, 0xe6"""
    runner = Runner()
    # Raw: 80fee6
    assert runner.run_test_bytes(
        name='ID_61: cmp dh, 0xe6',
        code=binascii.unhexlify('80fee6'),
        initial_regs={'EDX': 0xE600}, # DH is bits 8-15
        expected_regs={'EDX': 0xE600},
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_214_cmp_r8_m8():
    """Test: ID_214: cmp cl, byte ptr [ebp - 0xe]"""
    runner = Runner()
    # Raw: 3a4df2
    # 0x8000 - 0xE = 0x7FF2
    assert runner.run_test_bytes(
        name='ID_214: cmp cl, byte ptr [ebp - 0xe]',
        code=binascii.unhexlify('3a4df2'),
        initial_regs={'ECX': 0x55, 'EBP': 0x8000},
        expected_regs={'ECX': 0x55, 'EBP': 0x8000},
        expected_eflags=0x44, # ZF=1, PF=1
        expected_read={0x7FF2: 0x55}
    )

@pytest.mark.regression
def test_id_420_cmp_r8_r8():
    """Test: ID_420: cmp dl, al"""
    runner = Runner()
    # Raw: 38c2
    assert runner.run_test_bytes(
        name='ID_420: cmp dl, al',
        code=binascii.unhexlify('38c2'),
        initial_regs={'EDX': 0x77, 'EAX': 0x77},
        expected_regs={'EDX': 0x77, 'EAX': 0x77},
        expected_eflags=0x44 # ZF=1, PF=1
    )

@pytest.mark.regression
def test_id_442_cmpltpd_r128_m128():
    """Test: ID_442: cmpltpd xmm0, xmmword ptr [ebp - 0xf8]"""
    runner = Runner()
    # Raw: 660fc28508ffffff01
    # 0x8000 - 0xF8 = 0x7F08
    # 1.0 < 2.0 (True), 5.0 < 4.0 (False)
    # 1.0 = 0x3ff0000000000000, 2.0 = 0x4000000000000000
    # 5.0 = 0x4014000000000000, 4.0 = 0x4010000000000000
    v1_low = 0x3ff0000000000000
    v1_high = 0x4014000000000000
    m_low = 0x4000000000000000
    m_high = 0x4010000000000000
    
    assert runner.run_test_bytes(
        name='ID_442: cmpltpd xmm0, xmmword ptr [ebp - 0xf8]',
        code=binascii.unhexlify('660fc28508ffffff01'),
        initial_regs={
            'XMM0': v1_low | (v1_high << 64),
            'EBP': 0x8000
        },
        expected_regs={
            'XMM0': 0xFFFFFFFFFFFFFFFF | (0x0000000000000000 << 64)
        },
        expected_read={
            0x7F08: m_low,
            0x7F10: m_high
        }
    )

@pytest.mark.regression
def test_id_441_cmpltpd_r128_r128():
    """Test: ID_441: cmpltpd xmm2, xmm1"""
    runner = Runner()
    # Raw: 660fc2d101
    # xmm2[0] = 1.0, xmm1[0] = 2.0 -> True
    # xmm2[1] = 5.0, xmm1[1] = 4.0 -> False
    assert runner.run_test_bytes(
        name='ID_441: cmpltpd xmm2, xmm1',
        code=binascii.unhexlify('660fc2d101'),
        initial_regs={
            'XMM2': 0x3ff0000000000000 | (0x4014000000000000 << 64),
            'XMM1': 0x4000000000000000 | (0x4010000000000000 << 64)
        },
        expected_regs={
            'XMM2': 0xFFFFFFFFFFFFFFFF | (0x0000000000000000 << 64)
        }
    )

@pytest.mark.regression
def test_id_439_cmpltsd_r128_r128():
    """Test: ID_439: cmpltsd xmm2, xmm0"""
    runner = Runner()
    # Raw: f20fc2d001
    # Scalar: only low 64 bits affected
    assert runner.run_test_bytes(
        name='ID_439: cmpltsd xmm2, xmm0',
        code=binascii.unhexlify('f20fc2d001'),
        initial_regs={
            'XMM2': 0x3ff0000000000000 | (0x1111111111111111 << 64),
            'XMM0': 0x4000000000000000 | (0x2222222222222222 << 64)
        },
        expected_regs={
            'XMM2': 0xFFFFFFFFFFFFFFFF | (0x1111111111111111 << 64)
        }
    )

@pytest.mark.regression
def test_id_432_cmpneqsd_r128_r128():
    """Test: ID_432: cmpneqsd xmm2, xmm1"""
    runner = Runner()
    # Raw: f20fc2d104
    # 1.0 != 2.0 (True)
    assert runner.run_test_bytes(
        name='ID_432: cmpneqsd xmm2, xmm1',
        code=binascii.unhexlify('f20fc2d104'),
        initial_regs={
            'XMM2': 0x3ff0000000000000,
            'XMM1': 0x4000000000000000
        },
        expected_regs={
            'XMM2': 0xFFFFFFFFFFFFFFFF
        }
    )

@pytest.mark.regression
def test_id_356_cmpordsd_r128_r128():
    """Test: ID_356: cmpordsd xmm0, xmm1"""
    runner = Runner()
    # Raw: f20fc2c107
    # Ordered: neither is NaN
    assert runner.run_test_bytes(
        name='ID_356: cmpordsd xmm0, xmm1',
        code=binascii.unhexlify('f20fc2c107'),
        initial_regs={
            'XMM0': 0x3ff0000000000000,
            'XMM1': 0x4000000000000000
        },
        expected_regs={
            'XMM0': 0xFFFFFFFFFFFFFFFF
        }
    )

@pytest.mark.regression
def test_id_469_cvtdq2pd_r128_r128():
    """Test: ID_469: cvtdq2pd xmm0, xmm0"""
    runner = Runner()
    # Raw: f30fe6c0
    # int32: 1, 2 -> double: 1.0, 2.0
    assert runner.run_test_bytes(
        name='ID_469: cvtdq2pd xmm0, xmm0',
        code=binascii.unhexlify('f30fe6c0'),
        initial_regs={
            'XMM0': 1 | (2 << 32)
        },
        expected_regs={
            'XMM0': 0x3ff0000000000000 | (0x4000000000000000 << 64)
        }
    )

@pytest.mark.regression
def test_id_495_cvtdq2ps_r128_r128():
    """Test: ID_495: cvtdq2ps xmm0, xmm0"""
    runner = Runner()
    # Raw: 0f5bc0
    # int32: 1, 2, 3, 4 -> float32: 1.0, 2.0, 3.0, 4.0
    # 1.0f = 0x3f800000, 2.0f = 0x40000000, 3.0f = 0x40400000, 4.0f = 0x40800000
    assert runner.run_test_bytes(
        name='ID_495: cvtdq2ps xmm0, xmm0',
        code=binascii.unhexlify('0f5bc0'),
        initial_regs={
            'XMM0': 1 | (2 << 32) | (3 << 64) | (4 << 96)
        },
        expected_regs={
            'XMM0': 0x3f800000 | (0x40000000 << 32) | (0x40400000 << 64) | (0x40800000 << 96)
        }
    )

@pytest.mark.regression
def test_id_475_cvtpd2ps_r128_r128():
    """Test: ID_475: cvtpd2ps xmm1, xmm1"""
    runner = Runner()
    # Raw: 660f5ac9
    # double: 1.0, 2.0 -> float32: 1.0f, 2.0f (low 64 bits), high 64 bits zeroed
    assert runner.run_test_bytes(
        name='ID_475: cvtpd2ps xmm1, xmm1',
        code=binascii.unhexlify('660f5ac9'),
        initial_regs={
            'XMM1': 0x3ff0000000000000 | (0x4000000000000000 << 64)
        },
        expected_regs={
            'XMM1': 0x3f800000 | (0x40000000 << 32)
        }
    )

@pytest.mark.regression
def test_id_409_cvtsd2ss_r128_r128():
    """Test: ID_409: cvtsd2ss xmm1, xmm0"""
    runner = Runner()
    # Raw: f20f5ac8
    # scalar double -> scalar float
    assert runner.run_test_bytes(
        name='ID_409: cvtsd2ss xmm1, xmm0',
        code=binascii.unhexlify('f20f5ac8'),
        initial_regs={
            'XMM1': 0x11111111111111112222222222222222, # High parts should be preserved
            'XMM0': 0x4000000000000000 # 2.0
        },
        expected_regs={
            'XMM1': 0x40000000 | (0x111111111111111122222222 << 32) # Preserve higher bits of XMM1
        }
    )

@pytest.mark.regression
def test_id_415_cvtsi2sd_r128_m32():
    """Test: ID_415: cvtsi2sd xmm0, dword ptr [ebp - 0x38]"""
    runner = Runner()
    # Raw: f20f2a45c8
    # 0x8000 - 0x38 = 0x7FC8
    assert runner.run_test_bytes(
        name='ID_415: cvtsi2sd xmm0, dword ptr [ebp - 0x38]',
        code=binascii.unhexlify('f20f2a45c8'),
        initial_regs={
            'EBP': 0x8000,
            'XMM0': 0x11111111222222223333333344444444
        },
        expected_regs={
            'XMM0': 0x4000000000000000 | (0x1111111122222222 << 64)
        },
        expected_read={0x7FC8: 2}
    )

@pytest.mark.regression
def test_id_222_cvtsi2sd_r128_r32():
    """Test: ID_222: cvtsi2sd xmm0, eax"""
    runner = Runner()
    # Raw: f20f2ac0
    assert runner.run_test_bytes(
        name='ID_222: cvtsi2sd xmm0, eax',
        code=binascii.unhexlify('f20f2ac0'),
        initial_regs={
            'EAX': 3,
            'XMM0': 0x11111111222222223333333344444444
        },
        expected_regs={
            'XMM0': 0x4008000000000000 | (0x1111111122222222 << 64)
        }
    )

@pytest.mark.regression
def test_id_467_cvtsi2ss_r128_m32():
    """Test: ID_467: cvtsi2ss xmm0, dword ptr [ebp - 0x20]"""
    runner = Runner()
    # Raw: f30f2a45e0
    # 0x8000 - 0x20 = 0x7FE0
    assert runner.run_test_bytes(
        name='ID_467: cvtsi2ss xmm0, dword ptr [ebp - 0x20]',
        code=binascii.unhexlify('f30f2a45e0'),
        initial_regs={
            'EBP': 0x8000,
            'XMM0': 0x11111111222222223333333344444444
        },
        expected_regs={
            'XMM0': 0x40800000 | (0x11111111222222223333333344444444 >> 32 << 32)
        },
        expected_read={0x7FE0: 4}
    )

@pytest.mark.regression
def test_id_403_cvtsi2ss_r128_r32():
    """Test: ID_403: cvtsi2ss xmm3, eax"""
    runner = Runner()
    # Raw: f30f2ad8
    assert runner.run_test_bytes(
        name='ID_403: cvtsi2ss xmm3, eax',
        code=binascii.unhexlify('f30f2ad8'),
        initial_regs={
            'EAX': 5,
            'XMM3': 0x11111111222222223333333344444444
        },
        expected_regs={
            'XMM3': 0x40a00000 | (0x11111111222222223333333344444444 >> 32 << 32)
        }
    )

@pytest.mark.regression
def test_id_405_cvtss2sd_r128_r128():
    """Test: ID_405: cvtss2sd xmm0, xmm0"""
    runner = Runner()
    # Raw: f30f5ac0
    # scalar float to scalar double
    assert runner.run_test_bytes(
        name='ID_405: cvtss2sd xmm0, xmm0',
        code=binascii.unhexlify('f30f5ac0'),
        initial_regs={
            'XMM0': 0x40c00000 | (0x111111112222222233333333 << 32) # 6.0f
        },
        expected_regs={
            'XMM0': 0x4018000000000000 | (0x1111111122222222 << 64)
        }
    )

@pytest.mark.regression
def test_id_430_cvttpd2dq_r128_r128():
    """Test: ID_430: cvttpd2dq xmm1, xmm1"""
    runner = Runner()
    # Raw: 660fe6c9
    # double: 7.9, -8.1 -> int32: 7, -8 (low 64 bits), high 64 bits zeroed
    assert runner.run_test_bytes(
        name='ID_430: cvttpd2dq xmm1, xmm1',
        code=binascii.unhexlify('660fe6c9'),
        initial_regs={
            'XMM1': 0x401f99999999999a | (0xc020333333333333 << 64)
        },
        expected_regs={
            'XMM1': 7 | ((-8 & 0xFFFFFFFF) << 32)
        }
    )

@pytest.mark.regression
def test_id_383_cvttps2dq_r128_r128():
    """Test: ID_383: cvttps2dq xmm4, xmm4"""
    runner = Runner()
    # Raw: f30f5be4
    # float: 10.5, 11.5, 12.5, 13.5 -> int32: 10, 11, 12, 13
    assert runner.run_test_bytes(
        name='ID_383: cvttps2dq xmm4, xmm4',
        code=binascii.unhexlify('f30f5be4'),
        initial_regs={
            'XMM4': 0x41280000 | (0x41380000 << 32) | (0x41480000 << 64) | (0x41580000 << 96)
        },
        expected_regs={
            'XMM4': 10 | (11 << 32) | (12 << 64) | (13 << 96)
        }
    )