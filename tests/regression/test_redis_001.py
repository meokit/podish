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
    assert runner.run_test_bytes(
        name='ID_226: cmova ecx, ebx',
        code=binascii.unhexlify('0f47cb'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_150_cmovae_r32_r32():
    """Test: ID_150: cmovae edi, edx"""
    runner = Runner()
    # Raw: 0f43fa
    assert runner.run_test_bytes(
        name='ID_150: cmovae edi, edx',
        code=binascii.unhexlify('0f43fa'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_190_cmovb_r32_m32():
    """Test: ID_190: cmovb ecx, dword ptr [ebp - 0x18]"""
    runner = Runner()
    # Raw: 0f424de8
    assert runner.run_test_bytes(
        name='ID_190: cmovb ecx, dword ptr [ebp - 0x18]',
        code=binascii.unhexlify('0f424de8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_116_cmovb_r32_r32():
    """Test: ID_116: cmovb edx, edi"""
    runner = Runner()
    # Raw: 0f42d7
    assert runner.run_test_bytes(
        name='ID_116: cmovb edx, edi',
        code=binascii.unhexlify('0f42d7'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_418_cmovbe_r32_r32():
    """Test: ID_418: cmovbe ebx, edi"""
    runner = Runner()
    # Raw: 0f46df
    assert runner.run_test_bytes(
        name='ID_418: cmovbe ebx, edi',
        code=binascii.unhexlify('0f46df'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_124_cmove_r32_m32():
    """Test: ID_124: cmove edx, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 0f4455f0
    assert runner.run_test_bytes(
        name='ID_124: cmove edx, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('0f4455f0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_123_cmove_r32_r32():
    """Test: ID_123: cmove esi, edx"""
    runner = Runner()
    # Raw: 0f44f2
    assert runner.run_test_bytes(
        name='ID_123: cmove esi, edx',
        code=binascii.unhexlify('0f44f2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_426_cmovg_r32_m32():
    """Test: ID_426: cmovg ebx, dword ptr [ebp - 0x1c]"""
    runner = Runner()
    # Raw: 0f4f5de4
    assert runner.run_test_bytes(
        name='ID_426: cmovg ebx, dword ptr [ebp - 0x1c]',
        code=binascii.unhexlify('0f4f5de4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_212_cmovg_r32_r32():
    """Test: ID_212: cmovg eax, edx"""
    runner = Runner()
    # Raw: 0f4fc2
    assert runner.run_test_bytes(
        name='ID_212: cmovg eax, edx',
        code=binascii.unhexlify('0f4fc2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_185_cmovge_r32_r32():
    """Test: ID_185: cmovge esi, edi"""
    runner = Runner()
    # Raw: 0f4df7
    assert runner.run_test_bytes(
        name='ID_185: cmovge esi, edi',
        code=binascii.unhexlify('0f4df7'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_199_cmovl_r32_m32():
    """Test: ID_199: cmovl eax, dword ptr [ebp - 0x1c]"""
    runner = Runner()
    # Raw: 0f4c45e4
    assert runner.run_test_bytes(
        name='ID_199: cmovl eax, dword ptr [ebp - 0x1c]',
        code=binascii.unhexlify('0f4c45e4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_196_cmovl_r32_r32():
    """Test: ID_196: cmovl esi, ecx"""
    runner = Runner()
    # Raw: 0f4cf1
    assert runner.run_test_bytes(
        name='ID_196: cmovl esi, ecx',
        code=binascii.unhexlify('0f4cf1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_206_cmovle_r32_r32():
    """Test: ID_206: cmovle eax, edx"""
    runner = Runner()
    # Raw: 0f4ec2
    assert runner.run_test_bytes(
        name='ID_206: cmovle eax, edx',
        code=binascii.unhexlify('0f4ec2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_290_cmovne_r32_m32():
    """Test: ID_290: cmovne ebx, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 0f455df0
    assert runner.run_test_bytes(
        name='ID_290: cmovne ebx, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('0f455df0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_126_cmovne_r32_r32():
    """Test: ID_126: cmovne ecx, edx"""
    runner = Runner()
    # Raw: 0f45ca
    assert runner.run_test_bytes(
        name='ID_126: cmovne ecx, edx',
        code=binascii.unhexlify('0f45ca'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_479_cmovns_r32_m32():
    """Test: ID_479: cmovns esi, dword ptr [ebp - 0x28]"""
    runner = Runner()
    # Raw: 0f4975d8
    assert runner.run_test_bytes(
        name='ID_479: cmovns esi, dword ptr [ebp - 0x28]',
        code=binascii.unhexlify('0f4975d8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_137_cmovns_r32_r32():
    """Test: ID_137: cmovns eax, esi"""
    runner = Runner()
    # Raw: 0f49c6
    assert runner.run_test_bytes(
        name='ID_137: cmovns eax, esi',
        code=binascii.unhexlify('0f49c6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_464_cmovo_r32_r32():
    """Test: ID_464: cmovo eax, ecx"""
    runner = Runner()
    # Raw: 0f40c1
    assert runner.run_test_bytes(
        name='ID_464: cmovo eax, ecx',
        code=binascii.unhexlify('0f40c1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_207_cmovs_r32_m32():
    """Test: ID_207: cmovs ecx, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 0f484df0
    assert runner.run_test_bytes(
        name='ID_207: cmovs ecx, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('0f484df0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_297_cmovs_r32_r32():
    """Test: ID_297: cmovs edx, eax"""
    runner = Runner()
    # Raw: 0f48d0
    assert runner.run_test_bytes(
        name='ID_297: cmovs edx, eax',
        code=binascii.unhexlify('0f48d0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_178_cmp_m16_imm16():
    """Test: ID_178: cmp word ptr [ebp - 0x28], 0xa"""
    runner = Runner()
    # Raw: 66837dd80a
    assert runner.run_test_bytes(
        name='ID_178: cmp word ptr [ebp - 0x28], 0xa',
        code=binascii.unhexlify('66837dd80a'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_19_cmp_m32_imm32():
    """Test: ID_19: cmp dword ptr [eax + 0xc64], 3"""
    runner = Runner()
    # Raw: 83b8640c000003
    assert runner.run_test_bytes(
        name='ID_19: cmp dword ptr [eax + 0xc64], 3',
        code=binascii.unhexlify('83b8640c000003'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_115_cmp_m32_r32():
    """Test: ID_115: cmp dword ptr [edx + 4], edi"""
    runner = Runner()
    # Raw: 397a04
    assert runner.run_test_bytes(
        name='ID_115: cmp dword ptr [edx + 4], edi',
        code=binascii.unhexlify('397a04'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_37_cmp_m8_imm8():
    """Test: ID_37: cmp byte ptr [ebx + 0x28], 1"""
    runner = Runner()
    # Raw: 80bb2800000001
    assert runner.run_test_bytes(
        name='ID_37: cmp byte ptr [ebx + 0x28], 1',
        code=binascii.unhexlify('80bb2800000001'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_230_cmp_m8_r8():
    """Test: ID_230: cmp byte ptr [esi + 5], al"""
    runner = Runner()
    # Raw: 384605
    assert runner.run_test_bytes(
        name='ID_230: cmp byte ptr [esi + 5], al',
        code=binascii.unhexlify('384605'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_175_cmp_r16_imm16():
    """Test: ID_175: cmp ax, 3"""
    runner = Runner()
    # Raw: 6683f803
    assert runner.run_test_bytes(
        name='ID_175: cmp ax, 3',
        code=binascii.unhexlify('6683f803'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_322_cmp_r16_m16():
    """Test: ID_322: cmp ax, word ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 663b45f0
    assert runner.run_test_bytes(
        name='ID_322: cmp ax, word ptr [ebp - 0x10]',
        code=binascii.unhexlify('663b45f0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_215_cmp_r16_r16():
    """Test: ID_215: cmp cx, dx"""
    runner = Runner()
    # Raw: 6639d1
    assert runner.run_test_bytes(
        name='ID_215: cmp cx, dx',
        code=binascii.unhexlify('6639d1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_48_cmp_r32_imm32():
    """Test: ID_48: cmp esi, 8"""
    runner = Runner()
    # Raw: 83fe08
    assert runner.run_test_bytes(
        name='ID_48: cmp esi, 8',
        code=binascii.unhexlify('83fe08'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_51_cmp_r32_m32():
    """Test: ID_51: cmp esi, dword ptr [edi + 0x40]"""
    runner = Runner()
    # Raw: 3b7740
    assert runner.run_test_bytes(
        name='ID_51: cmp esi, dword ptr [edi + 0x40]',
        code=binascii.unhexlify('3b7740'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_93_cmp_r32_r32():
    """Test: ID_93: cmp esi, eax"""
    runner = Runner()
    # Raw: 39c6
    assert runner.run_test_bytes(
        name='ID_93: cmp esi, eax',
        code=binascii.unhexlify('39c6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_61_cmp_r8_imm8():
    """Test: ID_61: cmp dh, 0xe6"""
    runner = Runner()
    # Raw: 80fee6
    assert runner.run_test_bytes(
        name='ID_61: cmp dh, 0xe6',
        code=binascii.unhexlify('80fee6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_214_cmp_r8_m8():
    """Test: ID_214: cmp cl, byte ptr [ebp - 0xe]"""
    runner = Runner()
    # Raw: 3a4df2
    assert runner.run_test_bytes(
        name='ID_214: cmp cl, byte ptr [ebp - 0xe]',
        code=binascii.unhexlify('3a4df2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_420_cmp_r8_r8():
    """Test: ID_420: cmp dl, al"""
    runner = Runner()
    # Raw: 38c2
    assert runner.run_test_bytes(
        name='ID_420: cmp dl, al',
        code=binascii.unhexlify('38c2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_442_cmpltpd_r128_m128():
    """Test: ID_442: cmpltpd xmm0, xmmword ptr [ebp - 0xf8]"""
    runner = Runner()
    # Raw: 660fc28508ffffff01
    assert runner.run_test_bytes(
        name='ID_442: cmpltpd xmm0, xmmword ptr [ebp - 0xf8]',
        code=binascii.unhexlify('660fc28508ffffff01'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_441_cmpltpd_r128_r128():
    """Test: ID_441: cmpltpd xmm2, xmm1"""
    runner = Runner()
    # Raw: 660fc2d101
    assert runner.run_test_bytes(
        name='ID_441: cmpltpd xmm2, xmm1',
        code=binascii.unhexlify('660fc2d101'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_439_cmpltsd_r128_r128():
    """Test: ID_439: cmpltsd xmm2, xmm0"""
    runner = Runner()
    # Raw: f20fc2d001
    assert runner.run_test_bytes(
        name='ID_439: cmpltsd xmm2, xmm0',
        code=binascii.unhexlify('f20fc2d001'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_432_cmpneqsd_r128_r128():
    """Test: ID_432: cmpneqsd xmm2, xmm1"""
    runner = Runner()
    # Raw: f20fc2d104
    assert runner.run_test_bytes(
        name='ID_432: cmpneqsd xmm2, xmm1',
        code=binascii.unhexlify('f20fc2d104'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_356_cmpordsd_r128_r128():
    """Test: ID_356: cmpordsd xmm0, xmm1"""
    runner = Runner()
    # Raw: f20fc2c107
    assert runner.run_test_bytes(
        name='ID_356: cmpordsd xmm0, xmm1',
        code=binascii.unhexlify('f20fc2c107'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_469_cvtdq2pd_r128_r128():
    """Test: ID_469: cvtdq2pd xmm0, xmm0"""
    runner = Runner()
    # Raw: f30fe6c0
    assert runner.run_test_bytes(
        name='ID_469: cvtdq2pd xmm0, xmm0',
        code=binascii.unhexlify('f30fe6c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_495_cvtdq2ps_r128_r128():
    """Test: ID_495: cvtdq2ps xmm0, xmm0"""
    runner = Runner()
    # Raw: 0f5bc0
    assert runner.run_test_bytes(
        name='ID_495: cvtdq2ps xmm0, xmm0',
        code=binascii.unhexlify('0f5bc0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_475_cvtpd2ps_r128_r128():
    """Test: ID_475: cvtpd2ps xmm1, xmm1"""
    runner = Runner()
    # Raw: 660f5ac9
    assert runner.run_test_bytes(
        name='ID_475: cvtpd2ps xmm1, xmm1',
        code=binascii.unhexlify('660f5ac9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_409_cvtsd2ss_r128_r128():
    """Test: ID_409: cvtsd2ss xmm1, xmm0"""
    runner = Runner()
    # Raw: f20f5ac8
    assert runner.run_test_bytes(
        name='ID_409: cvtsd2ss xmm1, xmm0',
        code=binascii.unhexlify('f20f5ac8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_415_cvtsi2sd_r128_m32():
    """Test: ID_415: cvtsi2sd xmm0, dword ptr [ebp - 0x38]"""
    runner = Runner()
    # Raw: f20f2a45c8
    assert runner.run_test_bytes(
        name='ID_415: cvtsi2sd xmm0, dword ptr [ebp - 0x38]',
        code=binascii.unhexlify('f20f2a45c8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_222_cvtsi2sd_r128_r32():
    """Test: ID_222: cvtsi2sd xmm0, eax"""
    runner = Runner()
    # Raw: f20f2ac0
    assert runner.run_test_bytes(
        name='ID_222: cvtsi2sd xmm0, eax',
        code=binascii.unhexlify('f20f2ac0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_467_cvtsi2ss_r128_m32():
    """Test: ID_467: cvtsi2ss xmm0, dword ptr [ebp - 0x20]"""
    runner = Runner()
    # Raw: f30f2a45e0
    assert runner.run_test_bytes(
        name='ID_467: cvtsi2ss xmm0, dword ptr [ebp - 0x20]',
        code=binascii.unhexlify('f30f2a45e0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_403_cvtsi2ss_r128_r32():
    """Test: ID_403: cvtsi2ss xmm3, eax"""
    runner = Runner()
    # Raw: f30f2ad8
    assert runner.run_test_bytes(
        name='ID_403: cvtsi2ss xmm3, eax',
        code=binascii.unhexlify('f30f2ad8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_405_cvtss2sd_r128_r128():
    """Test: ID_405: cvtss2sd xmm0, xmm0"""
    runner = Runner()
    # Raw: f30f5ac0
    assert runner.run_test_bytes(
        name='ID_405: cvtss2sd xmm0, xmm0',
        code=binascii.unhexlify('f30f5ac0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_430_cvttpd2dq_r128_r128():
    """Test: ID_430: cvttpd2dq xmm1, xmm1"""
    runner = Runner()
    # Raw: 660fe6c9
    assert runner.run_test_bytes(
        name='ID_430: cvttpd2dq xmm1, xmm1',
        code=binascii.unhexlify('660fe6c9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_383_cvttps2dq_r128_r128():
    """Test: ID_383: cvttps2dq xmm4, xmm4"""
    runner = Runner()
    # Raw: f30f5be4
    assert runner.run_test_bytes(
        name='ID_383: cvttps2dq xmm4, xmm4',
        code=binascii.unhexlify('f30f5be4'),
        initial_regs={},
        expected_regs={}
    )

