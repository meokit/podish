# Redis Regression Test Batch 004
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_284_movd_m32_r128():
    """Test: ID_284: movd dword ptr [ebp - 0x28], xmm6"""
    runner = Runner()
    # Raw: 660f7e75d8
    assert runner.run_test_bytes(
        name='ID_284: movd dword ptr [ebp - 0x28], xmm6',
        code=binascii.unhexlify('660f7e75d8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_271_movd_r128_m32():
    """Test: ID_271: movd xmm2, dword ptr [eax + 0x30]"""
    runner = Runner()
    # Raw: 660f6e5030
    assert runner.run_test_bytes(
        name='ID_271: movd xmm2, dword ptr [eax + 0x30]',
        code=binascii.unhexlify('660f6e5030'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_262_movd_r128_r32():
    """Test: ID_262: movd xmm0, ebx"""
    runner = Runner()
    # Raw: 660f6ec3
    assert runner.run_test_bytes(
        name='ID_262: movd xmm0, ebx',
        code=binascii.unhexlify('660f6ec3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_281_movd_r32_r128():
    """Test: ID_281: movd ebx, xmm0"""
    runner = Runner()
    # Raw: 660f7ec3
    assert runner.run_test_bytes(
        name='ID_281: movd ebx, xmm0',
        code=binascii.unhexlify('660f7ec3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_266_movdqa_m128_r128():
    """Test: ID_266: movdqa xmmword ptr [ebp - 0x78], xmm0"""
    runner = Runner()
    # Raw: 660f7f4588
    assert runner.run_test_bytes(
        name='ID_266: movdqa xmmword ptr [ebp - 0x78], xmm0',
        code=binascii.unhexlify('660f7f4588'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_265_movdqa_r128_m128():
    """Test: ID_265: movdqa xmm0, xmmword ptr [edi]"""
    runner = Runner()
    # Raw: 660f6f8700000000
    assert runner.run_test_bytes(
        name='ID_265: movdqa xmm0, xmmword ptr [edi]',
        code=binascii.unhexlify('660f6f8700000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_272_movdqa_r128_r128():
    """Test: ID_272: movdqa xmm2, xmm3"""
    runner = Runner()
    # Raw: 660f6fd3
    assert runner.run_test_bytes(
        name='ID_272: movdqa xmm2, xmm3',
        code=binascii.unhexlify('660f6fd3'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_296_movdqu_m128_r128():
    """Test: ID_296: movdqu xmmword ptr [ecx], xmm1"""
    runner = Runner()
    # Raw: f30f7f09
    assert runner.run_test_bytes(
        name='ID_296: movdqu xmmword ptr [ecx], xmm1',
        code=binascii.unhexlify('f30f7f09'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_267_movdqu_r128_m128():
    """Test: ID_267: movdqu xmm6, xmmword ptr [eax + 0x20]"""
    runner = Runner()
    # Raw: f30f6f7020
    assert runner.run_test_bytes(
        name='ID_267: movdqu xmm6, xmmword ptr [eax + 0x20]',
        code=binascii.unhexlify('f30f6f7020'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_427_movhpd_r128_m64():
    """Test: ID_427: movhpd xmm2, qword ptr [ebp - 0x100]"""
    runner = Runner()
    # Raw: 660f169500ffffff
    assert runner.run_test_bytes(
        name='ID_427: movhpd xmm2, qword ptr [ebp - 0x100]',
        code=binascii.unhexlify('660f169500ffffff'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_429_movhps_m64_r128():
    """Test: ID_429: movhps qword ptr [esp], xmm0"""
    runner = Runner()
    # Raw: 0f170424
    assert runner.run_test_bytes(
        name='ID_429: movhps qword ptr [esp], xmm0',
        code=binascii.unhexlify('0f170424'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_428_movlpd_m64_r128():
    """Test: ID_428: movlpd qword ptr [esp], xmm2"""
    runner = Runner()
    # Raw: 660f131424
    assert runner.run_test_bytes(
        name='ID_428: movlpd qword ptr [esp], xmm2',
        code=binascii.unhexlify('660f131424'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_344_movlps_m64_r128():
    """Test: ID_344: movlps qword ptr [eax], xmm0"""
    runner = Runner()
    # Raw: 0f1300
    assert runner.run_test_bytes(
        name='ID_344: movlps qword ptr [eax], xmm0',
        code=binascii.unhexlify('0f1300'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_308_movmskps_r32_r128():
    """Test: ID_308: movmskps eax, xmm0"""
    runner = Runner()
    # Raw: 0f50c0
    assert runner.run_test_bytes(
        name='ID_308: movmskps eax, xmm0',
        code=binascii.unhexlify('0f50c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_310_movq_m64_r128():
    """Test: ID_310: movq qword ptr [ebp - 0x48], xmm0"""
    runner = Runner()
    # Raw: 660fd645b8
    assert runner.run_test_bytes(
        name='ID_310: movq qword ptr [ebp - 0x48], xmm0',
        code=binascii.unhexlify('660fd645b8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_283_movq_r128_m64():
    """Test: ID_283: movq xmm0, qword ptr [eax + 0x14]"""
    runner = Runner()
    # Raw: f30f7e4014
    assert runner.run_test_bytes(
        name='ID_283: movq xmm0, qword ptr [eax + 0x14]',
        code=binascii.unhexlify('f30f7e4014'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_264_movq_r128_r128():
    """Test: ID_264: movq xmm1, xmm0"""
    runner = Runner()
    # Raw: f30f7ec8
    assert runner.run_test_bytes(
        name='ID_264: movq xmm1, xmm0',
        code=binascii.unhexlify('f30f7ec8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_66_movsd_m64_r128():
    """Test: ID_66: movsd qword ptr [edi + 0x50], xmm0"""
    runner = Runner()
    # Raw: f20f114750
    assert runner.run_test_bytes(
        name='ID_66: movsd qword ptr [edi + 0x50], xmm0',
        code=binascii.unhexlify('f20f114750'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_65_movsd_r128_m64():
    """Test: ID_65: movsd xmm0, qword ptr [esi + 0x20]"""
    runner = Runner()
    # Raw: f20f104620
    assert runner.run_test_bytes(
        name='ID_65: movsd xmm0, qword ptr [esi + 0x20]',
        code=binascii.unhexlify('f20f104620'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_378_movsd_r128_r128():
    """Test: ID_378: movsd xmm6, xmm7"""
    runner = Runner()
    # Raw: f20f10f7
    assert runner.run_test_bytes(
        name='ID_378: movsd xmm6, xmm7',
        code=binascii.unhexlify('f20f10f7'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_240_movss_m32_r128():
    """Test: ID_240: movss dword ptr [esi + 0x10], xmm0"""
    runner = Runner()
    # Raw: f30f114610
    assert runner.run_test_bytes(
        name='ID_240: movss dword ptr [esi + 0x10], xmm0',
        code=binascii.unhexlify('f30f114610'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_239_movss_r128_m32():
    """Test: ID_239: movss xmm0, dword ptr [esi + 0x10]"""
    runner = Runner()
    # Raw: f30f104610
    assert runner.run_test_bytes(
        name='ID_239: movss xmm0, dword ptr [esi + 0x10]',
        code=binascii.unhexlify('f30f104610'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_316_movsx_r32_m16():
    """Test: ID_316: movsx eax, word ptr [ecx]"""
    runner = Runner()
    # Raw: 0fbf01
    assert runner.run_test_bytes(
        name='ID_316: movsx eax, word ptr [ecx]',
        code=binascii.unhexlify('0fbf01'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_84_movsx_r32_m8():
    """Test: ID_84: movsx eax, byte ptr [esi]"""
    runner = Runner()
    # Raw: 0fbe06
    assert runner.run_test_bytes(
        name='ID_84: movsx eax, byte ptr [esi]',
        code=binascii.unhexlify('0fbe06'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_315_movsx_r32_r16():
    """Test: ID_315: movsx eax, cx"""
    runner = Runner()
    # Raw: 0fbfc1
    assert runner.run_test_bytes(
        name='ID_315: movsx eax, cx',
        code=binascii.unhexlify('0fbfc1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_235_movsx_r32_r8():
    """Test: ID_235: movsx edx, al"""
    runner = Runner()
    # Raw: 0fbed0
    assert runner.run_test_bytes(
        name='ID_235: movsx edx, al',
        code=binascii.unhexlify('0fbed0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_392_movupd_m128_r128():
    """Test: ID_392: movupd xmmword ptr [esi + 0x1c], xmm2"""
    runner = Runner()
    # Raw: 660f11561c
    assert runner.run_test_bytes(
        name='ID_392: movupd xmmword ptr [esi + 0x1c], xmm2',
        code=binascii.unhexlify('660f11561c'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_238_movups_m128_r128():
    """Test: ID_238: movups xmmword ptr [esi], xmm0"""
    runner = Runner()
    # Raw: 0f1106
    assert runner.run_test_bytes(
        name='ID_238: movups xmmword ptr [esi], xmm0',
        code=binascii.unhexlify('0f1106'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_236_movups_r128_m128():
    """Test: ID_236: movups xmm0, xmmword ptr [esi]"""
    runner = Runner()
    # Raw: 0f1006
    assert runner.run_test_bytes(
        name='ID_236: movups xmm0, xmmword ptr [esi]',
        code=binascii.unhexlify('0f1006'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_73_movzx_r32_m16():
    """Test: ID_73: movzx ecx, word ptr [eax - 5]"""
    runner = Runner()
    # Raw: 0fb748fb
    assert runner.run_test_bytes(
        name='ID_73: movzx ecx, word ptr [eax - 5]',
        code=binascii.unhexlify('0fb748fb'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_58_movzx_r32_m8():
    """Test: ID_58: movzx edx, byte ptr [esi + ecx + 0x27]"""
    runner = Runner()
    # Raw: 0fb6540e27
    assert runner.run_test_bytes(
        name='ID_58: movzx edx, byte ptr [esi + ecx + 0x27]',
        code=binascii.unhexlify('0fb6540e27'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_160_movzx_r32_r16():
    """Test: ID_160: movzx eax, ax"""
    runner = Runner()
    # Raw: 0fb7c0
    assert runner.run_test_bytes(
        name='ID_160: movzx eax, ax',
        code=binascii.unhexlify('0fb7c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_85_movzx_r32_r8():
    """Test: ID_85: movzx eax, dl"""
    runner = Runner()
    # Raw: 0fb6c2
    assert runner.run_test_bytes(
        name='ID_85: movzx eax, dl',
        code=binascii.unhexlify('0fb6c2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_291_mul_m32():
    """Test: ID_291: mul dword ptr [ebp - 0x18]"""
    runner = Runner()
    # Raw: f765e8
    assert runner.run_test_bytes(
        name='ID_291: mul dword ptr [ebp - 0x18]',
        code=binascii.unhexlify('f765e8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_162_mul_r32():
    """Test: ID_162: mul ecx"""
    runner = Runner()
    # Raw: f7e1
    assert runner.run_test_bytes(
        name='ID_162: mul ecx',
        code=binascii.unhexlify('f7e1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_393_mulpd_r128_m128():
    """Test: ID_393: mulpd xmm0, xmmword ptr [eax]"""
    runner = Runner()
    # Raw: 660f598000000000
    assert runner.run_test_bytes(
        name='ID_393: mulpd xmm0, xmmword ptr [eax]',
        code=binascii.unhexlify('660f598000000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_390_mulpd_r128_r128():
    """Test: ID_390: mulpd xmm3, xmm6"""
    runner = Runner()
    # Raw: 660f59de
    assert runner.run_test_bytes(
        name='ID_390: mulpd xmm3, xmm6',
        code=binascii.unhexlify('660f59de'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_476_mulps_r128_m128():
    """Test: ID_476: mulps xmm1, xmmword ptr [ebx]"""
    runner = Runner()
    # Raw: 0f598b00000000
    assert runner.run_test_bytes(
        name='ID_476: mulps xmm1, xmmword ptr [ebx]',
        code=binascii.unhexlify('0f598b00000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_250_mulsd_r128_m64():
    """Test: ID_250: mulsd xmm0, qword ptr [ebx]"""
    runner = Runner()
    # Raw: f20f598300000000
    assert runner.run_test_bytes(
        name='ID_250: mulsd xmm0, qword ptr [ebx]',
        code=binascii.unhexlify('f20f598300000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_381_mulsd_r128_r128():
    """Test: ID_381: mulsd xmm1, xmm2"""
    runner = Runner()
    # Raw: f20f59ca
    assert runner.run_test_bytes(
        name='ID_381: mulsd xmm1, xmm2',
        code=binascii.unhexlify('f20f59ca'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_438_mulss_r128_m32():
    """Test: ID_438: mulss xmm0, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: f30f5945f0
    assert runner.run_test_bytes(
        name='ID_438: mulss xmm0, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('f30f5945f0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_491_mulss_r128_r128():
    """Test: ID_491: mulss xmm0, xmm2"""
    runner = Runner()
    # Raw: f30f59c2
    assert runner.run_test_bytes(
        name='ID_491: mulss xmm0, xmm2',
        code=binascii.unhexlify('f30f59c2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_423_neg_m32():
    """Test: ID_423: neg dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: f75df0
    assert runner.run_test_bytes(
        name='ID_423: neg dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('f75df0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_40_neg_r32():
    """Test: ID_40: neg eax"""
    runner = Runner()
    # Raw: f7d8
    assert runner.run_test_bytes(
        name='ID_40: neg eax',
        code=binascii.unhexlify('f7d8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_292_neg_r8():
    """Test: ID_292: neg al"""
    runner = Runner()
    # Raw: f6d8
    assert runner.run_test_bytes(
        name='ID_292: neg al',
        code=binascii.unhexlify('f6d8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_31_nop_no_operands():
    """Test: ID_31: nop """
    runner = Runner()
    # Raw: 90
    assert runner.run_test_bytes(
        name='ID_31: nop ',
        code=binascii.unhexlify('90'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_35_nop_m16():
    """Test: ID_35: nop word ptr cs:[eax + eax]"""
    runner = Runner()
    # Raw: 662e0f1f840000000000
    assert runner.run_test_bytes(
        name='ID_35: nop word ptr cs:[eax + eax]',
        code=binascii.unhexlify('662e0f1f840000000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_36_nop_m32():
    """Test: ID_36: nop dword ptr [eax]"""
    runner = Runner()
    # Raw: 0f1f00
    assert runner.run_test_bytes(
        name='ID_36: nop dword ptr [eax]',
        code=binascii.unhexlify('0f1f00'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_143_not_r32():
    """Test: ID_143: not eax"""
    runner = Runner()
    # Raw: f7d0
    assert runner.run_test_bytes(
        name='ID_143: not eax',
        code=binascii.unhexlify('f7d0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_133_not_r8():
    """Test: ID_133: not dl"""
    runner = Runner()
    # Raw: f6d2
    assert runner.run_test_bytes(
        name='ID_133: not dl',
        code=binascii.unhexlify('f6d2'),
        initial_regs={},
        expected_regs={}
    )

