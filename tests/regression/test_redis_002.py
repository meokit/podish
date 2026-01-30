# Redis Regression Test Batch 002
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_422_cvttsd2si_r32_m64():
    """Test: ID_422: cvttsd2si ecx, qword ptr [ebp - 0x28]"""
    runner = Runner()
    # Raw: f20f2c4dd8
    assert runner.run_test_bytes(
        name='ID_422: cvttsd2si ecx, qword ptr [ebp - 0x28]',
        code=binascii.unhexlify('f20f2c4dd8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_382_cvttsd2si_r32_r128():
    """Test: ID_382: cvttsd2si eax, xmm1"""
    runner = Runner()
    # Raw: f20f2cc1
    assert runner.run_test_bytes(
        name='ID_382: cvttsd2si eax, xmm1',
        code=binascii.unhexlify('f20f2cc1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_458_cvttss2si_r32_r128():
    """Test: ID_458: cvttss2si ebx, xmm0"""
    runner = Runner()
    # Raw: f30f2cd8
    assert runner.run_test_bytes(
        name='ID_458: cvttss2si ebx, xmm0',
        code=binascii.unhexlify('f30f2cd8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_496_cwde_no_operands():
    """Test: ID_496: cwde """
    runner = Runner()
    # Raw: 98
    assert runner.run_test_bytes(
        name='ID_496: cwde ',
        code=binascii.unhexlify('98'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_437_dec_m16():
    """Test: ID_437: dec word ptr [esi + 0x18]"""
    runner = Runner()
    # Raw: 66ff4e18
    assert runner.run_test_bytes(
        name='ID_437: dec word ptr [esi + 0x18]',
        code=binascii.unhexlify('66ff4e18'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_134_dec_m32():
    """Test: ID_134: dec dword ptr [edi + 0x86c]"""
    runner = Runner()
    # Raw: ff8f6c080000
    assert runner.run_test_bytes(
        name='ID_134: dec dword ptr [edi + 0x86c]',
        code=binascii.unhexlify('ff8f6c080000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_67_dec_r32():
    """Test: ID_67: dec edi"""
    runner = Runner()
    # Raw: 4f
    assert runner.run_test_bytes(
        name='ID_67: dec edi',
        code=binascii.unhexlify('4f'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_287_dec_r8():
    """Test: ID_287: dec cl"""
    runner = Runner()
    # Raw: fec9
    assert runner.run_test_bytes(
        name='ID_287: dec cl',
        code=binascii.unhexlify('fec9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_324_div_m32():
    """Test: ID_324: div dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: f775f0
    assert runner.run_test_bytes(
        name='ID_324: div dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('f775f0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_323_div_r32():
    """Test: ID_323: div esi"""
    runner = Runner()
    # Raw: f7f6
    assert runner.run_test_bytes(
        name='ID_323: div esi',
        code=binascii.unhexlify('f7f6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_389_divpd_r128_r128():
    """Test: ID_389: divpd xmm6, xmm4"""
    runner = Runner()
    # Raw: 660f5ef4
    assert runner.run_test_bytes(
        name='ID_389: divpd xmm6, xmm4',
        code=binascii.unhexlify('660f5ef4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_477_divps_r128_r128():
    """Test: ID_477: divps xmm1, xmm0"""
    runner = Runner()
    # Raw: 0f5ec8
    assert runner.run_test_bytes(
        name='ID_477: divps xmm1, xmm0',
        code=binascii.unhexlify('0f5ec8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_312_divsd_r128_m64():
    """Test: ID_312: divsd xmm0, qword ptr [ebp - 0x38]"""
    runner = Runner()
    # Raw: f20f5e45c8
    assert runner.run_test_bytes(
        name='ID_312: divsd xmm0, qword ptr [ebp - 0x38]',
        code=binascii.unhexlify('f20f5e45c8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_248_divsd_r128_r128():
    """Test: ID_248: divsd xmm0, xmm2"""
    runner = Runner()
    # Raw: f20f5ec2
    assert runner.run_test_bytes(
        name='ID_248: divsd xmm0, xmm2',
        code=binascii.unhexlify('f20f5ec2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_408_divss_r128_m32():
    """Test: ID_408: divss xmm0, dword ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: f30f5e45f0
    assert runner.run_test_bytes(
        name='ID_408: divss xmm0, dword ptr [ebp - 0x10]',
        code=binascii.unhexlify('f30f5e45f0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_404_divss_r128_r128():
    """Test: ID_404: divss xmm2, xmm1"""
    runner = Runner()
    # Raw: f30f5ed1
    assert runner.run_test_bytes(
        name='ID_404: divss xmm2, xmm1',
        code=binascii.unhexlify('f30f5ed1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_424_fadd_m32():
    """Test: ID_424: fadd dword ptr [ebx + eax*4]"""
    runner = Runner()
    # Raw: d8848300000000
    assert runner.run_test_bytes(
        name='ID_424: fadd dword ptr [ebx + eax*4]',
        code=binascii.unhexlify('d8848300000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_335_faddp_r80():
    """Test: ID_335: faddp st(1)"""
    runner = Runner()
    # Raw: dec1
    assert runner.run_test_bytes(
        name='ID_335: faddp st(1)',
        code=binascii.unhexlify('dec1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_474_fild_m32():
    """Test: ID_474: fild dword ptr [ebp - 8]"""
    runner = Runner()
    # Raw: db45f8
    assert runner.run_test_bytes(
        name='ID_474: fild dword ptr [ebp - 8]',
        code=binascii.unhexlify('db45f8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_311_fild_m64():
    """Test: ID_311: fild qword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: df6db8
    assert runner.run_test_bytes(
        name='ID_311: fild qword ptr [ebp - 0x48]',
        code=binascii.unhexlify('df6db8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_254_fistp_m64():
    """Test: ID_254: fistp qword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: df7db8
    assert runner.run_test_bytes(
        name='ID_254: fistp qword ptr [ebp - 0x48]',
        code=binascii.unhexlify('df7db8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_482_fld_m32():
    """Test: ID_482: fld dword ptr [ebp - 0x14]"""
    runner = Runner()
    # Raw: d945ec
    assert runner.run_test_bytes(
        name='ID_482: fld dword ptr [ebp - 0x14]',
        code=binascii.unhexlify('d945ec'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_251_fld_m64():
    """Test: ID_251: fld qword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: dd45b8
    assert runner.run_test_bytes(
        name='ID_251: fld qword ptr [ebp - 0x48]',
        code=binascii.unhexlify('dd45b8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_334_fld_m80():
    """Test: ID_334: fld xword ptr [ebp - 0x40]"""
    runner = Runner()
    # Raw: db6dc0
    assert runner.run_test_bytes(
        name='ID_334: fld xword ptr [ebp - 0x40]',
        code=binascii.unhexlify('db6dc0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_336_fld_r80():
    """Test: ID_336: fld st(0)"""
    runner = Runner()
    # Raw: d9c0
    assert runner.run_test_bytes(
        name='ID_336: fld st(0)',
        code=binascii.unhexlify('d9c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_253_fldcw_m16():
    """Test: ID_253: fldcw word ptr [ebp - 0x3a]"""
    runner = Runner()
    # Raw: d96dc6
    assert runner.run_test_bytes(
        name='ID_253: fldcw word ptr [ebp - 0x3a]',
        code=binascii.unhexlify('d96dc6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_425_fldz_no_operands():
    """Test: ID_425: fldz """
    runner = Runner()
    # Raw: d9ee
    assert runner.run_test_bytes(
        name='ID_425: fldz ',
        code=binascii.unhexlify('d9ee'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_483_fmul_m32():
    """Test: ID_483: fmul dword ptr [ebx]"""
    runner = Runner()
    # Raw: d88b00000000
    assert runner.run_test_bytes(
        name='ID_483: fmul dword ptr [ebx]',
        code=binascii.unhexlify('d88b00000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_252_fnstcw_m16():
    """Test: ID_252: fnstcw word ptr [ebp - 0x2a]"""
    runner = Runner()
    # Raw: d97dd6
    assert runner.run_test_bytes(
        name='ID_252: fnstcw word ptr [ebp - 0x2a]',
        code=binascii.unhexlify('d97dd6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_402_fstp_m32():
    """Test: ID_402: fstp dword ptr [ebp - 0x30]"""
    runner = Runner()
    # Raw: d95dd0
    assert runner.run_test_bytes(
        name='ID_402: fstp dword ptr [ebp - 0x30]',
        code=binascii.unhexlify('d95dd0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_242_fstp_m64():
    """Test: ID_242: fstp qword ptr [ebp - 0x80]"""
    runner = Runner()
    # Raw: dd5d80
    assert runner.run_test_bytes(
        name='ID_242: fstp qword ptr [ebp - 0x80]',
        code=binascii.unhexlify('dd5d80'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_337_fstp_m80():
    """Test: ID_337: fstp xword ptr [ebp - 0x28]"""
    runner = Runner()
    # Raw: db7dd8
    assert runner.run_test_bytes(
        name='ID_337: fstp xword ptr [ebp - 0x28]',
        code=binascii.unhexlify('db7dd8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_338_fstp_r80():
    """Test: ID_338: fstp st(0)"""
    runner = Runner()
    # Raw: ddd8
    assert runner.run_test_bytes(
        name='ID_338: fstp st(0)',
        code=binascii.unhexlify('ddd8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_470_fucomi_r80():
    """Test: ID_470: fucomi st(0)"""
    runner = Runner()
    # Raw: dbe8
    assert runner.run_test_bytes(
        name='ID_470: fucomi st(0)',
        code=binascii.unhexlify('dbe8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_473_fucompi_r80():
    """Test: ID_473: fucompi st(1)"""
    runner = Runner()
    # Raw: dfe9
    assert runner.run_test_bytes(
        name='ID_473: fucompi st(1)',
        code=binascii.unhexlify('dfe9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_472_fxch_r80_r80():
    """Test: ID_472: fxch st(1)"""
    runner = Runner()
    # Raw: d9c9
    assert runner.run_test_bytes(
        name='ID_472: fxch st(1)',
        code=binascii.unhexlify('d9c9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_255_idiv_m32():
    """Test: ID_255: idiv dword ptr [esi + 0x20]"""
    runner = Runner()
    # Raw: f77e20
    assert runner.run_test_bytes(
        name='ID_255: idiv dword ptr [esi + 0x20]',
        code=binascii.unhexlify('f77e20'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_487_idiv_r16():
    """Test: ID_487: idiv cx"""
    runner = Runner()
    # Raw: 66f7f9
    assert runner.run_test_bytes(
        name='ID_487: idiv cx',
        code=binascii.unhexlify('66f7f9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_257_idiv_r32():
    """Test: ID_257: idiv esi"""
    runner = Runner()
    # Raw: f7fe
    assert runner.run_test_bytes(
        name='ID_257: idiv esi',
        code=binascii.unhexlify('f7fe'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_200_imul_m32():
    """Test: ID_200: imul dword ptr [ebx + 0xee4]"""
    runner = Runner()
    # Raw: f7abe40e0000
    assert runner.run_test_bytes(
        name='ID_200: imul dword ptr [ebx + 0xee4]',
        code=binascii.unhexlify('f7abe40e0000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_198_imul_r32():
    """Test: ID_198: imul ecx"""
    runner = Runner()
    # Raw: f7e9
    assert runner.run_test_bytes(
        name='ID_198: imul ecx',
        code=binascii.unhexlify('f7e9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_339_imul_r32_m32():
    """Test: ID_339: imul eax, dword ptr [ebp - 0x14]"""
    runner = Runner()
    # Raw: 0faf45ec
    assert runner.run_test_bytes(
        name='ID_339: imul eax, dword ptr [ebp - 0x14]',
        code=binascii.unhexlify('0faf45ec'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_172_imul_r32_m32_imm32():
    """Test: ID_172: imul eax, dword ptr [ebp + 0xc], 0x68"""
    runner = Runner()
    # Raw: 6b450c68
    assert runner.run_test_bytes(
        name='ID_172: imul eax, dword ptr [ebp + 0xc], 0x68',
        code=binascii.unhexlify('6b450c68'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_201_imul_r32_r32():
    """Test: ID_201: imul ebx, eax"""
    runner = Runner()
    # Raw: 0fafd8
    assert runner.run_test_bytes(
        name='ID_201: imul ebx, eax',
        code=binascii.unhexlify('0fafd8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_49_imul_r32_r32_imm32():
    """Test: ID_49: imul ecx, esi, 0x58"""
    runner = Runner()
    # Raw: 6bce58
    assert runner.run_test_bytes(
        name='ID_49: imul ecx, esi, 0x58',
        code=binascii.unhexlify('6bce58'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_416_inc_m16():
    """Test: ID_416: inc word ptr [ecx + 0x10]"""
    runner = Runner()
    # Raw: 66ff4110
    assert runner.run_test_bytes(
        name='ID_416: inc word ptr [ecx + 0x10]',
        code=binascii.unhexlify('66ff4110'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_164_inc_m32():
    """Test: ID_164: inc dword ptr [ebp - 0x3c]"""
    runner = Runner()
    # Raw: ff45c4
    assert runner.run_test_bytes(
        name='ID_164: inc dword ptr [ebp - 0x3c]',
        code=binascii.unhexlify('ff45c4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_447_inc_m8():
    """Test: ID_447: inc byte ptr [esi - 3]"""
    runner = Runner()
    # Raw: fe46fd
    assert runner.run_test_bytes(
        name='ID_447: inc byte ptr [esi - 3]',
        code=binascii.unhexlify('fe46fd'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_47_inc_r32():
    """Test: ID_47: inc esi"""
    runner = Runner()
    # Raw: 46
    assert runner.run_test_bytes(
        name='ID_47: inc esi',
        code=binascii.unhexlify('46'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_216_inc_r8():
    """Test: ID_216: inc ah"""
    runner = Runner()
    # Raw: fec4
    assert runner.run_test_bytes(
        name='ID_216: inc ah',
        code=binascii.unhexlify('fec4'),
        initial_regs={},
        expected_regs={}
    )

