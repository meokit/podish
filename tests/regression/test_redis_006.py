# Redis Regression Test Batch 006
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_340_punpcklqdq_r128_m128():
    """Test: ID_340: punpcklqdq xmm1, xmmword ptr [ebx]"""
    runner = Runner()
    # Raw: 660f6c8b00000000
    assert runner.run_test_bytes(
        name='ID_340: punpcklqdq xmm1, xmmword ptr [ebx]',
        code=binascii.unhexlify('660f6c8b00000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_360_punpcklqdq_r128_r128():
    """Test: ID_360: punpcklqdq xmm4, xmm3"""
    runner = Runner()
    # Raw: 660f6ce3
    assert runner.run_test_bytes(
        name='ID_360: punpcklqdq xmm4, xmm3',
        code=binascii.unhexlify('660f6ce3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_303_punpcklwd_r128_r128():
    """Test: ID_303: punpcklwd xmm2, xmm2"""
    runner = Runner()
    # Raw: 660f61d2
    assert runner.run_test_bytes(
        name='ID_303: punpcklwd xmm2, xmm2',
        code=binascii.unhexlify('660f61d2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_33_push_imm32():
    """Test: ID_33: push 0x3e"""
    runner = Runner()
    # Raw: 6a3e
    assert runner.run_test_bytes(
        name='ID_33: push 0x3e',
        code=binascii.unhexlify('6a3e'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_46_push_m32():
    """Test: ID_46: push dword ptr [ebp - 0x1c]"""
    runner = Runner()
    # Raw: ff75e4
    assert runner.run_test_bytes(
        name='ID_46: push dword ptr [ebp - 0x1c]',
        code=binascii.unhexlify('ff75e4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_1_push_r32():
    """Test: ID_1: push ebp"""
    runner = Runner()
    # Raw: 55
    assert runner.run_test_bytes(
        name='ID_1: push ebp',
        code=binascii.unhexlify('55'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_279_pxor_r128_r128():
    """Test: ID_279: pxor xmm0, xmm0"""
    runner = Runner()
    # Raw: 660fefc0
    assert runner.run_test_bytes(
        name='ID_279: pxor xmm0, xmm0',
        code=binascii.unhexlify('660fefc0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_223_rep_movsd_m32_m32():
    """Test: ID_223: rep movsd dword ptr es:[edi], dword ptr [esi]"""
    runner = Runner()
    # Raw: f3a5
    assert runner.run_test_bytes(
        name='ID_223: rep movsd dword ptr es:[edi], dword ptr [esi]',
        code=binascii.unhexlify('f3a5'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_307_rep_stosd_m32_r32():
    """Test: ID_307: rep stosd dword ptr es:[edi], eax"""
    runner = Runner()
    # Raw: f3ab
    assert runner.run_test_bytes(
        name='ID_307: rep stosd dword ptr es:[edi], eax',
        code=binascii.unhexlify('f3ab'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_30_ret_no_operands():
    """Test: ID_30: ret """
    runner = Runner()
    # Raw: c3
    assert runner.run_test_bytes(
        name='ID_30: ret ',
        code=binascii.unhexlify('c3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_443_ret_imm32():
    """Test: ID_443: ret 4"""
    runner = Runner()
    # Raw: c20400
    assert runner.run_test_bytes(
        name='ID_443: ret 4',
        code=binascii.unhexlify('c20400'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_327_rol_m16_imm8():
    """Test: ID_327: rol word ptr [eax], 8"""
    runner = Runner()
    # Raw: 66c10008
    assert runner.run_test_bytes(
        name='ID_327: rol word ptr [eax], 8',
        code=binascii.unhexlify('66c10008'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_159_rol_r16_imm8():
    """Test: ID_159: rol cx, 8"""
    runner = Runner()
    # Raw: 66c1c108
    assert runner.run_test_bytes(
        name='ID_159: rol cx, 8',
        code=binascii.unhexlify('66c1c108'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_478_rol_r32_imm32():
    """Test: ID_478: rol esi, 1"""
    runner = Runner()
    # Raw: d1c6
    assert runner.run_test_bytes(
        name='ID_478: rol esi, 1',
        code=binascii.unhexlify('d1c6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_414_rol_r32_imm8():
    """Test: ID_414: rol ecx, 0xf"""
    runner = Runner()
    # Raw: c1c10f
    assert runner.run_test_bytes(
        name='ID_414: rol ecx, 0xf',
        code=binascii.unhexlify('c1c10f'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_481_rol_r8_imm8():
    """Test: ID_481: rol al, 4"""
    runner = Runner()
    # Raw: c0c004
    assert runner.run_test_bytes(
        name='ID_481: rol al, 4',
        code=binascii.unhexlify('c0c004'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_462_ror_r32_imm8():
    """Test: ID_462: ror eax, 2"""
    runner = Runner()
    # Raw: c1c802
    assert runner.run_test_bytes(
        name='ID_462: ror eax, 2',
        code=binascii.unhexlify('c1c802'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_399_ror_r32_r8():
    """Test: ID_399: ror esi, cl"""
    runner = Runner()
    # Raw: d3ce
    assert runner.run_test_bytes(
        name='ID_399: ror esi, cl',
        code=binascii.unhexlify('d3ce'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_152_sar_r32_imm32():
    """Test: ID_152: sar esi, 1"""
    runner = Runner()
    # Raw: d1fe
    assert runner.run_test_bytes(
        name='ID_152: sar esi, 1',
        code=binascii.unhexlify('d1fe'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_138_sar_r32_imm8():
    """Test: ID_138: sar eax, 3"""
    runner = Runner()
    # Raw: c1f803
    assert runner.run_test_bytes(
        name='ID_138: sar eax, 3',
        code=binascii.unhexlify('c1f803'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_400_sar_r32_r8():
    """Test: ID_400: sar eax, cl"""
    runner = Runner()
    # Raw: d3f8
    assert runner.run_test_bytes(
        name='ID_400: sar eax, cl',
        code=binascii.unhexlify('d3f8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_485_sar_r8_imm8():
    """Test: ID_485: sar cl, 7"""
    runner = Runner()
    # Raw: c0f907
    assert runner.run_test_bytes(
        name='ID_485: sar cl, 7',
        code=binascii.unhexlify('c0f907'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_188_sbb_m32_imm32():
    """Test: ID_188: sbb dword ptr [edi + 0x18], 0"""
    runner = Runner()
    # Raw: 835f1800
    assert runner.run_test_bytes(
        name='ID_188: sbb dword ptr [edi + 0x18], 0',
        code=binascii.unhexlify('835f1800'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_220_sbb_m32_r32():
    """Test: ID_220: sbb dword ptr [ebp - 0x10], edx"""
    runner = Runner()
    # Raw: 1955f0
    assert runner.run_test_bytes(
        name='ID_220: sbb dword ptr [ebp - 0x10], edx',
        code=binascii.unhexlify('1955f0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_195_sbb_r32_imm32():
    """Test: ID_195: sbb edx, 0"""
    runner = Runner()
    # Raw: 83da00
    assert runner.run_test_bytes(
        name='ID_195: sbb edx, 0',
        code=binascii.unhexlify('83da00'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_146_sbb_r32_m32():
    """Test: ID_146: sbb edi, dword ptr [ecx + 8]"""
    runner = Runner()
    # Raw: 1b7908
    assert runner.run_test_bytes(
        name='ID_146: sbb edi, dword ptr [ecx + 8]',
        code=binascii.unhexlify('1b7908'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_41_sbb_r32_r32():
    """Test: ID_41: sbb esi, esi"""
    runner = Runner()
    # Raw: 19f6
    assert runner.run_test_bytes(
        name='ID_41: sbb esi, esi',
        code=binascii.unhexlify('19f6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_353_sbb_r8_imm8():
    """Test: ID_353: sbb al, 0"""
    runner = Runner()
    # Raw: 1c00
    assert runner.run_test_bytes(
        name='ID_353: sbb al, 0',
        code=binascii.unhexlify('1c00'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_318_seta_r8():
    """Test: ID_318: seta dl"""
    runner = Runner()
    # Raw: 0f97c2
    assert runner.run_test_bytes(
        name='ID_318: seta dl',
        code=binascii.unhexlify('0f97c2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_234_setae_r8():
    """Test: ID_234: setae cl"""
    runner = Runner()
    # Raw: 0f93c1
    assert runner.run_test_bytes(
        name='ID_234: setae cl',
        code=binascii.unhexlify('0f93c1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_320_setb_m8():
    """Test: ID_320: setb byte ptr [ebp - 0xd]"""
    runner = Runner()
    # Raw: 0f9245f3
    assert runner.run_test_bytes(
        name='ID_320: setb byte ptr [ebp - 0xd]',
        code=binascii.unhexlify('0f9245f3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_62_setb_r8():
    """Test: ID_62: setb dh"""
    runner = Runner()
    # Raw: 0f92c6
    assert runner.run_test_bytes(
        name='ID_62: setb dh',
        code=binascii.unhexlify('0f92c6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_349_setbe_r8():
    """Test: ID_349: setbe bl"""
    runner = Runner()
    # Raw: 0f96c3
    assert runner.run_test_bytes(
        name='ID_349: setbe bl',
        code=binascii.unhexlify('0f96c3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_101_sete_m8():
    """Test: ID_101: sete byte ptr [ebp - 0x38]"""
    runner = Runner()
    # Raw: 0f9445c8
    assert runner.run_test_bytes(
        name='ID_101: sete byte ptr [ebp - 0x38]',
        code=binascii.unhexlify('0f9445c8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_83_sete_r8():
    """Test: ID_83: sete al"""
    runner = Runner()
    # Raw: 0f94c0
    assert runner.run_test_bytes(
        name='ID_83: sete al',
        code=binascii.unhexlify('0f94c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_468_setg_m8():
    """Test: ID_468: setg byte ptr [ebp - 0x20]"""
    runner = Runner()
    # Raw: 0f9f45e0
    assert runner.run_test_bytes(
        name='ID_468: setg byte ptr [ebp - 0x20]',
        code=binascii.unhexlify('0f9f45e0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_186_setg_r8():
    """Test: ID_186: setg dl"""
    runner = Runner()
    # Raw: 0f9fc2
    assert runner.run_test_bytes(
        name='ID_186: setg dl',
        code=binascii.unhexlify('0f9fc2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_341_setge_m8():
    """Test: ID_341: setge byte ptr [ebp - 0x25]"""
    runner = Runner()
    # Raw: 0f9d45db
    assert runner.run_test_bytes(
        name='ID_341: setge byte ptr [ebp - 0x25]',
        code=binascii.unhexlify('0f9d45db'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_202_setge_r8():
    """Test: ID_202: setge cl"""
    runner = Runner()
    # Raw: 0f9dc1
    assert runner.run_test_bytes(
        name='ID_202: setge cl',
        code=binascii.unhexlify('0f9dc1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_289_setl_m8():
    """Test: ID_289: setl byte ptr [ebp - 0xd]"""
    runner = Runner()
    # Raw: 0f9c45f3
    assert runner.run_test_bytes(
        name='ID_289: setl byte ptr [ebp - 0xd]',
        code=binascii.unhexlify('0f9c45f3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_191_setl_r8():
    """Test: ID_191: setl cl"""
    runner = Runner()
    # Raw: 0f9cc1
    assert runner.run_test_bytes(
        name='ID_191: setl cl',
        code=binascii.unhexlify('0f9cc1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_231_setle_r8():
    """Test: ID_231: setle dl"""
    runner = Runner()
    # Raw: 0f9ec2
    assert runner.run_test_bytes(
        name='ID_231: setle dl',
        code=binascii.unhexlify('0f9ec2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_259_setne_m8():
    """Test: ID_259: setne byte ptr [ebp - 0x14]"""
    runner = Runner()
    # Raw: 0f9545ec
    assert runner.run_test_bytes(
        name='ID_259: setne byte ptr [ebp - 0x14]',
        code=binascii.unhexlify('0f9545ec'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_55_setne_r8():
    """Test: ID_55: setne cl"""
    runner = Runner()
    # Raw: 0f95c1
    assert runner.run_test_bytes(
        name='ID_55: setne cl',
        code=binascii.unhexlify('0f95c1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_298_setns_m8():
    """Test: ID_298: setns byte ptr [ebp - 0x23]"""
    runner = Runner()
    # Raw: 0f9945dd
    assert runner.run_test_bytes(
        name='ID_298: setns byte ptr [ebp - 0x23]',
        code=binascii.unhexlify('0f9945dd'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_261_setns_r8():
    """Test: ID_261: setns dl"""
    runner = Runner()
    # Raw: 0f99c2
    assert runner.run_test_bytes(
        name='ID_261: setns dl',
        code=binascii.unhexlify('0f99c2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_471_setp_r8():
    """Test: ID_471: setp cl"""
    runner = Runner()
    # Raw: 0f9ac1
    assert runner.run_test_bytes(
        name='ID_471: setp cl',
        code=binascii.unhexlify('0f9ac1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_218_sets_r8():
    """Test: ID_218: sets dh"""
    runner = Runner()
    # Raw: 0f98c6
    assert runner.run_test_bytes(
        name='ID_218: sets dh',
        code=binascii.unhexlify('0f98c6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_373_shl_m32_imm8():
    """Test: ID_373: shl dword ptr [ebp - 0x24], 0x18"""
    runner = Runner()
    # Raw: c165dc18
    assert runner.run_test_bytes(
        name='ID_373: shl dword ptr [ebp - 0x24], 0x18',
        code=binascii.unhexlify('c165dc18'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_99_shl_r32_imm8():
    """Test: ID_99: shl edi, 4"""
    runner = Runner()
    # Raw: c1e704
    assert runner.run_test_bytes(
        name='ID_99: shl edi, 4',
        code=binascii.unhexlify('c1e704'),
        initial_regs={},
        expected_regs={}
    )

