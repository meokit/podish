# Redis Regression Test Batch 000
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_170_adc_m32_imm32():
    """Test: ID_170: adc dword ptr [esi + 0xdb0], 0"""
    runner = Runner()
    # Raw: 8396b00d000000
    assert runner.run_test_bytes(
        name='ID_170: adc dword ptr [esi + 0xdb0], 0',
        code=binascii.unhexlify('8396b00d000000'),
        initial_regs={'ESI': 0x2000},
        expected_regs={},
        initial_eflags=1, # CF=1
        expected_write={0x2db0: 1} # 0 + 0 + 1 = 1
    )

@pytest.mark.regression
def test_id_204_adc_m32_r32():
    """Test: ID_204: adc dword ptr [edx + 0x30028], ecx"""
    runner = Runner()
    # Raw: 118a28000300
    assert runner.run_test_bytes(
        name='ID_204: adc dword ptr [edx + 0x30028], ecx',
        code=binascii.unhexlify('118a28000300'),
        initial_regs={'EDX': (0x2000 - 0x30028) & 0xFFFFFFFF, 'ECX': 0x55},
        expected_regs={},
        initial_eflags=0,
        expected_write={0x2000: 0x55}
    )

@pytest.mark.regression
def test_id_461_adc_r16_imm16():
    """Test: ID_461: adc cx, -1"""
    runner = Runner()
    # Raw: 6683d1ff
    assert runner.run_test_bytes(
        name='ID_461: adc cx, -1',
        code=binascii.unhexlify('6683d1ff'),
        initial_regs={'ECX': 0x10},
        expected_regs={'ECX': 0x10},
        initial_eflags=1 # CF=1
    )

@pytest.mark.regression
def test_id_79_adc_r32_imm32():
    """Test: ID_79: adc eax, -1"""
    runner = Runner()
    # Raw: 83d0ff
    assert runner.run_test_bytes(
        name='ID_79: adc eax, -1',
        code=binascii.unhexlify('83d0ff'),
        initial_regs={'EAX': 0x10},
        expected_regs={'EAX': 0x10},
        initial_eflags=1 # CF=1
    )

@pytest.mark.regression
def test_id_176_adc_r32_m32():
    """Test: ID_176: adc edx, dword ptr [ebp - 0x18]"""
    runner = Runner()
    # Raw: 1355e8
    assert runner.run_test_bytes(
        name='ID_176: adc edx, dword ptr [ebp - 0x18]',
        code=binascii.unhexlify('1355e8'),
        initial_regs={'EBP': 0x2100, 'EDX': 0x100}, # Addr = 0x20E8 (0 value)
        expected_regs={'EDX': 0x100},
        initial_eflags=0
    )

@pytest.mark.regression
def test_id_147_adc_r32_r32():
    """Test: ID_147: adc eax, edx"""
    runner = Runner()
    # Raw: 11d0
    assert runner.run_test_bytes(
        name='ID_147: adc eax, edx',
        code=binascii.unhexlify('11d0'),
        initial_regs={'EAX': 0x100, 'EDX': 0x200},
        expected_regs={'EAX': 0x301},
        initial_eflags=1 # CF=1
    )

@pytest.mark.regression
def test_id_450_adc_r8_imm8():
    """Test: ID_450: adc bl, 1"""
    runner = Runner()
    # Raw: 80d301
    assert runner.run_test_bytes(
        name='ID_450: adc bl, 1',
        code=binascii.unhexlify('80d301'),
        initial_regs={'EBX': 0xFE},
        expected_regs={'EBX': 0x0}, # BL wraps to 0, upper bytes 0
        initial_eflags=1 # CF=1
    )

@pytest.mark.regression
def test_id_449_add_m16_r16():
    """Test: ID_449: add word ptr [esi - 5], dx"""
    runner = Runner()
    # Raw: 660156fb
    assert runner.run_test_bytes(
        name='ID_449: add word ptr [esi - 5], dx',
        code=binascii.unhexlify('660156fb'),
        initial_regs={'ESI': 0x2010, 'EDX': 0x1234},
        expected_regs={},
        expected_write={0x200B: 0x1234}
    )

@pytest.mark.regression
def test_id_140_add_m32_imm32():
    """Test: ID_140: add dword ptr [edx + 0x78c], 0x418"""
    runner = Runner()
    # Raw: 81828c07000018040000
    assert runner.run_test_bytes(
        name='ID_140: add dword ptr [edx + 0x78c], 0x418',
        code=binascii.unhexlify('81828c07000018040000'),
        initial_regs={'EDX': 0x2000 - 0x78c},
        expected_regs={},
        expected_write={0x2000: 0x418}
    )

@pytest.mark.regression
def test_id_165_add_m32_r32():
    """Test: ID_165: add dword ptr [ebp - 0x38], edx"""
    runner = Runner()
    # Raw: 0155c8
    assert runner.run_test_bytes(
        name='ID_165: add dword ptr [ebp - 0x38], edx',
        code=binascii.unhexlify('0155c8'),
        initial_regs={'EBP': 0x2100, 'EDX': 0x55},
        expected_regs={},
        expected_write={0x20C8: 0x55}
    )

@pytest.mark.regression
def test_id_448_add_m8_r8():
    """Test: ID_448: add byte ptr [esi - 3], dl"""
    runner = Runner()
    # Raw: 0056fd
    assert runner.run_test_bytes(
        name='ID_448: add byte ptr [esi - 3], dl',
        code=binascii.unhexlify('0056fd'),
        initial_regs={'ESI': 0x2003, 'EDX': 0x10},
        expected_regs={},
        expected_write={0x2000: 0x10}
    )

@pytest.mark.regression
def test_id_445_add_r16_m16():
    """Test: ID_445: add cx, word ptr [eax - 5]"""
    runner = Runner()
    # Raw: 660348fb
    assert runner.run_test_bytes(
        name='ID_445: add cx, word ptr [eax - 5]',
        code=binascii.unhexlify('660348fb'),
        initial_regs={'EAX': 0x2005, 'ECX': 0x10},
        expected_regs={'ECX': 0x10}
    )

@pytest.mark.regression
def test_id_9_add_r32_imm32():
    """Test: ID_9: add ebx, 3"""
    runner = Runner()
    # Raw: 81c303000000
    assert runner.run_test_bytes(
        name='ID_9: add ebx, 3',
        code=binascii.unhexlify('81c303000000'),
        initial_regs={'EBX': 0x10},
        expected_regs={'EBX': 0x13}
    )

@pytest.mark.regression
def test_id_75_add_r32_m32():
    """Test: ID_75: add edx, dword ptr [edx + esi*4 + 0x14]"""
    runner = Runner()
    # Raw: 0394b214000000
    assert runner.run_test_bytes(
        name='ID_75: add edx, dword ptr [edx + esi*4 + 0x14]',
        code=binascii.unhexlify('0394b214000000'),
        initial_regs={'EDX': 0x1FEC, 'ESI': 0},
        expected_regs={'EDX': 0x1FEC}
    )

@pytest.mark.regression
def test_id_45_add_r32_r32():
    """Test: ID_45: add esi, eax"""
    runner = Runner()
    # Raw: 01c6
    assert runner.run_test_bytes(
        name='ID_45: add esi, eax',
        code=binascii.unhexlify('01c6'),
        initial_regs={'ESI': 10, 'EAX': 20},
        expected_regs={'ESI': 30}
    )

@pytest.mark.regression
def test_id_60_add_r8_imm8():
    """Test: ID_60: add dh, 0x85"""
    runner = Runner()
    # Raw: 80c685
    assert runner.run_test_bytes(
        name='ID_60: add dh, 0x85',
        code=binascii.unhexlify('80c685'),
        initial_regs={'EDX': 0x1234},
        expected_regs={'EDX': 0x9734}
    )

@pytest.mark.regression
def test_id_446_add_r8_m8():
    """Test: ID_446: add cl, byte ptr [eax - 3]"""
    runner = Runner()
    # Raw: 0248fd
    assert runner.run_test_bytes(
        name='ID_446: add cl, byte ptr [eax - 3]',
        code=binascii.unhexlify('0248fd'),
        initial_regs={'EAX': 0x2003, 'ECX': 0x10},
        expected_regs={'ECX': 0x10}
    )

@pytest.mark.regression
def test_id_345_add_r8_r8():
    """Test: ID_345: add cl, cl"""
    runner = Runner()
    # Raw: 00c9
    assert runner.run_test_bytes(
        name='ID_345: add cl, cl',
        code=binascii.unhexlify('00c9'),
        initial_regs={'ECX': 0x10},
        expected_regs={'ECX': 0x20}
    )

@pytest.mark.regression
def test_id_394_addpd_r128_m128():
    """Test: ID_394: addpd xmm0, xmmword ptr [eax]"""
    runner = Runner()
    # Raw: 660f588000000000
    assert runner.run_test_bytes(
        name='ID_394: addpd xmm0, xmmword ptr [eax]',
        code=binascii.unhexlify('660f588000000000'),
        initial_regs={'EAX': 0x2000},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_391_addpd_r128_r128():
    """Test: ID_391: addpd xmm2, xmm3"""
    runner = Runner()
    # Raw: 660f58d3
    assert runner.run_test_bytes(
        name='ID_391: addpd xmm2, xmm3',
        code=binascii.unhexlify('660f58d3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_366_addsd_r128_m64():
    """Test: ID_366: addsd xmm0, qword ptr [ebx]"""
    runner = Runner()
    # Raw: f20f588300000000
    assert runner.run_test_bytes(
        name='ID_366: addsd xmm0, qword ptr [ebx]',
        code=binascii.unhexlify('f20f588300000000'),
        initial_regs={'EBX': 0x2000},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_350_addsd_r128_r128():
    """Test: ID_350: addsd xmm1, xmm0"""
    runner = Runner()
    # Raw: f20f58c8
    assert runner.run_test_bytes(
        name='ID_350: addsd xmm1, xmm0',
        code=binascii.unhexlify('f20f58c8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_457_addss_r128_m32():
    """Test: ID_457: addss xmm0, dword ptr [ebp - 0x3c]"""
    runner = Runner()
    # Raw: f30f5845c4
    assert runner.run_test_bytes(
        name='ID_457: addss xmm0, dword ptr [ebp - 0x3c]',
        code=binascii.unhexlify('f30f5845c4'),
        initial_regs={'EBP': 0x2100},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_494_addss_r128_r128():
    """Test: ID_494: addss xmm1, xmm0"""
    runner = Runner()
    # Raw: f30f58c8
    assert runner.run_test_bytes(
        name='ID_494: addss xmm1, xmm0',
        code=binascii.unhexlify('f30f58c8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_456_and_m16_imm16():
    """Test: ID_456: and word ptr [ecx + 8], 0xfbbf"""
    runner = Runner()
    # Raw: 66816108bffb
    assert runner.run_test_bytes(
        name='ID_456: and word ptr [ecx + 8], 0xfbbf',
        code=binascii.unhexlify('66816108bffb'),
        initial_regs={'ECX': 0x2000},
        expected_regs={},
        expected_write={0x2008: 0}
    )

@pytest.mark.regression
def test_id_117_and_m32_imm32():
    """Test: ID_117: and dword ptr [eax + 0x30064], 0xfffffffb"""
    runner = Runner()
    # Raw: 83a064000300fb
    assert runner.run_test_bytes(
        name='ID_117: and dword ptr [eax + 0x30064], 0xfffffffb',
        code=binascii.unhexlify('83a064000300fb'),
        initial_regs={'EAX': (0x2000 - 0x30064) & 0xFFFFFFFF},
        expected_regs={},
        expected_write={0x2000: 0}
    )

@pytest.mark.regression
def test_id_306_and_m32_r32():
    """Test: ID_306: and dword ptr [ecx + esi*8 + 4], edx"""
    runner = Runner()
    # Raw: 2154f104
    assert runner.run_test_bytes(
        name='ID_306: and dword ptr [ecx + esi*8 + 4], edx',
        code=binascii.unhexlify('2154f104'),
        initial_regs={'ECX': 0x2000, 'ESI': 0, 'EDX': 0xFFFFFFFF},
        expected_regs={},
        expected_write={0x2004: 0}
    )

@pytest.mark.regression
def test_id_128_and_m8_imm8():
    """Test: ID_128: and byte ptr [eax + 0x59], 0xfe"""
    runner = Runner()
    # Raw: 806059fe
    assert runner.run_test_bytes(
        name='ID_128: and byte ptr [eax + 0x59], 0xfe',
        code=binascii.unhexlify('806059fe'),
        initial_regs={'EAX': 0x2000},
        expected_regs={},
        expected_write={0x2059: 0}
    )

@pytest.mark.regression
def test_id_135_and_m8_r8():
    """Test: ID_135: and byte ptr [ecx + eax + 0x30128], dl"""
    runner = Runner()
    # Raw: 20940128010300
    assert runner.run_test_bytes(
        name='ID_135: and byte ptr [ecx + eax + 0x30128], dl',
        code=binascii.unhexlify('20940128010300'),
        initial_regs={'ECX': 0, 'EAX': (0x2000 - 0x30128) & 0xFFFFFFFF, 'EDX': 0xFF},
        expected_regs={},
        expected_write={0x2000: 0}
    )

@pytest.mark.regression
def test_id_69_and_r32_imm32():
    """Test: ID_69: and edx, 7"""
    runner = Runner()
    # Raw: 83e207
    assert runner.run_test_bytes(
        name='ID_69: and edx, 7',
        code=binascii.unhexlify('83e207'),
        initial_regs={'EDX': 15},
        expected_regs={'EDX': 7}
    )

@pytest.mark.regression
def test_id_180_and_r32_m32():
    """Test: ID_180: and eax, dword ptr [esi + 0x30054]"""
    runner = Runner()
    # Raw: 238654000300
    assert runner.run_test_bytes(
        name='ID_180: and eax, dword ptr [esi + 0x30054]',
        code=binascii.unhexlify('238654000300'),
        initial_regs={'ESI': (0x2000 - 0x30054) & 0xFFFFFFFF, 'EAX': 0xFFFFFFFF},
        expected_regs={'EAX': 0}
    )

@pytest.mark.regression
def test_id_208_and_r32_r32():
    """Test: ID_208: and eax, ecx"""
    runner = Runner()
    # Raw: 21c8
    assert runner.run_test_bytes(
        name='ID_208: and eax, ecx',
        code=binascii.unhexlify('21c8'),
        initial_regs={'EAX': 0xF, 'ECX': 3},
        expected_regs={'EAX': 3}
    )

@pytest.mark.regression
def test_id_130_and_r8_imm8():
    """Test: ID_130: and cl, 7"""
    runner = Runner()
    # Raw: 80e107
    assert runner.run_test_bytes(
        name='ID_130: and cl, 7',
        code=binascii.unhexlify('80e107'),
        initial_regs={'ECX': 15},
        expected_regs={'ECX': 7}
    )

@pytest.mark.regression
def test_id_182_and_r8_m8():
    """Test: ID_182: and al, byte ptr [ebp - 0x24]"""
    runner = Runner()
    # Raw: 2245dc
    assert runner.run_test_bytes(
        name='ID_182: and al, byte ptr [ebp - 0x24]',
        code=binascii.unhexlify('2245dc'),
        initial_regs={'EBP': 0x2100, 'EAX': 0xFF},
        expected_regs={'EAX': 0}
    )

@pytest.mark.regression
def test_id_63_and_r8_r8():
    """Test: ID_63: and dl, dh"""
    runner = Runner()
    # Raw: 20f2
    assert runner.run_test_bytes(
        name='ID_63: and dl, dh',
        code=binascii.unhexlify('20f2'),
        initial_regs={'EDX': 0x070F},
        expected_regs={'EDX': 0x0707}
    )

@pytest.mark.regression
def test_id_440_andnpd_r128_r128():
    """Test: ID_440: andnpd xmm1, xmm0"""
    runner = Runner()
    # Raw: 660f55c8
    assert runner.run_test_bytes(
        name='ID_440: andnpd xmm1, xmm0',
        code=binascii.unhexlify('660f55c8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_396_andpd_r128_m128():
    """Test: ID_396: andpd xmm4, xmmword ptr [esi]"""
    runner = Runner()
    # Raw: 660f54a600000000
    assert runner.run_test_bytes(
        name='ID_396: andpd xmm4, xmmword ptr [esi]',
        code=binascii.unhexlify('660f54a600000000'),
        initial_regs={'ESI': 0x2000},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_357_andpd_r128_r128():
    """Test: ID_357: andpd xmm0, xmm1"""
    runner = Runner()
    # Raw: 660f54c1
    assert runner.run_test_bytes(
        name='ID_357: andpd xmm0, xmm1',
        code=binascii.unhexlify('660f54c1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_362_andps_r128_m128():
    """Test: ID_362: andps xmm2, xmmword ptr [ebp - 0x28]"""
    runner = Runner()
    # Raw: 0f5455d8
    assert runner.run_test_bytes(
        name='ID_362: andps xmm2, xmmword ptr [ebp - 0x28]',
        code=binascii.unhexlify('0f5455d8'),
        initial_regs={'EBP': 0x2100},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_363_andps_r128_r128():
    """Test: ID_363: andps xmm7, xmm1"""
    runner = Runner()
    # Raw: 0f54f9
    assert runner.run_test_bytes(
        name='ID_363: andps xmm7, xmm1',
        code=binascii.unhexlify('0f54f9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_435_bsr_r32_r32():
    """Test: ID_435: bsr eax, eax"""
    runner = Runner()
    # Raw: 0fbdc0
    assert runner.run_test_bytes(
        name='ID_435: bsr eax, eax',
        code=binascii.unhexlify('0fbdc0'),
        initial_regs={'EAX': 0x10},
        expected_regs={'EAX': 4},
        initial_eflags=0
    )

@pytest.mark.regression
def test_id_161_bswap_r32():
    """Test: ID_161: bswap eax"""
    runner = Runner()
    # Raw: 0fc8
    assert runner.run_test_bytes(
        name='ID_161: bswap eax',
        code=binascii.unhexlify('0fc8'),
        initial_regs={'EAX': 0x12345678},
        expected_regs={'EAX': 0x78563412}
    )

@pytest.mark.regression
def test_id_453_bt_m32_imm8():
    """Test: ID_453: bt dword ptr [eax], 5"""
    runner = Runner()
    # Raw: 0fba2005
    assert runner.run_test_bytes(
        name='ID_453: bt dword ptr [eax], 5',
        code=binascii.unhexlify('0fba2005'),
        initial_regs={'EAX': 0x2000},
        expected_regs={},
        initial_eflags=0,
        expected_eflags=0 # CF=0 (Bit 5 of 0 is 0)
    )

@pytest.mark.regression
def test_id_211_bt_r32_imm8():
    """Test: ID_211: bt ebx, 8"""
    runner = Runner()
    # Raw: 0fbae308
    assert runner.run_test_bytes(
        name='ID_211: bt ebx, 8',
        code=binascii.unhexlify('0fbae308'),
        initial_regs={'EBX': 0x100},
        expected_regs={},
        initial_eflags=0,
        expected_eflags=1 # CF=1
    )

@pytest.mark.regression
def test_id_132_bt_r32_r32():
    """Test: ID_132: bt ecx, esi"""
    runner = Runner()
    # Raw: 0fa3f1
    assert runner.run_test_bytes(
        name='ID_132: bt ecx, esi',
        code=binascii.unhexlify('0fa3f1'),
        initial_regs={'ECX': 0x10, 'ESI': 4},
        expected_regs={},
        initial_eflags=0,
        expected_eflags=1 # CF=1
    )

@pytest.mark.regression
def test_id_286_btr_r32_r32():
    """Test: ID_286: btr esi, ecx"""
    runner = Runner()
    # Raw: 0fb3ce
    assert runner.run_test_bytes(
        name='ID_286: btr esi, ecx',
        code=binascii.unhexlify('0fb3ce'),
        initial_regs={'ESI': 0x10, 'ECX': 4},
        expected_regs={'ESI': 0},
        initial_eflags=0,
        expected_eflags=1 # CF=1
    )

@pytest.mark.regression
def test_id_7_call_imm32():
    """Test: ID_7: call 0x1005"""
    runner = Runner()
    # Raw: e800000000
    assert runner.run_test_bytes(
        name='ID_7: call 0x1005',
        code=binascii.unhexlify('e800000000'),
        initial_regs={'ESP': 0x8000},
        expected_regs={'ESP': 0x7FFC},
        expected_eip=0x1005,
        expected_write={0x7FFC: 0x1005} # Pushes return address (next instruction)
    )

@pytest.mark.regression
def test_id_12_call_m32():
    """Test: ID_12: call dword ptr [eax]"""
    runner = Runner()
    # Raw: ff10
    assert runner.run_test_bytes(
        name='ID_12: call dword ptr [eax]',
        code=binascii.unhexlify('ff10'),
        initial_regs={'EAX': 0x2000, 'ESP': 0x8000},
        expected_regs={'ESP': 0x7FFC},
        # Target address read from [0x2000] is 0.
        expected_eip=0,
        expected_write={0x7FFC: 0x1002} # Return address
    )

@pytest.mark.regression
def test_id_25_call_r32():
    """Test: ID_25: call eax"""
    runner = Runner()
    # Raw: ffd0
    assert runner.run_test_bytes(
        name='ID_25: call eax',
        code=binascii.unhexlify('ffd0'),
        initial_regs={'EAX': 0x1005, 'ESP': 0x8000},
        expected_regs={'ESP': 0x7FFC},
        expected_eip=0x1005,
        expected_write={0x7FFC: 0x1002}
    )

@pytest.mark.regression
def test_id_256_cdq_no_operands():
    """Test: ID_256: cdq """
    runner = Runner()
    # Raw: 99
    assert runner.run_test_bytes(
        name='ID_256: cdq ',
        code=binascii.unhexlify('99'),
        initial_regs={'EAX': 0x80000000},
        expected_regs={'EDX': 0xFFFFFFFF}
    )

