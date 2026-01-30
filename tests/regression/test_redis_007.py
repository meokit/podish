# Redis Regression Test Batch 007
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
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
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_224_shl_r8_imm8():
    """Test: ID_224: shl al, 2"""
    runner = Runner()
    # Raw: c0e002
    assert runner.run_test_bytes(
        name='ID_224: shl al, 2',
        code=binascii.unhexlify('c0e002'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_300_shl_r8_r8():
    """Test: ID_300: shl al, cl"""
    runner = Runner()
    # Raw: d2e0
    assert runner.run_test_bytes(
        name='ID_300: shl al, cl',
        code=binascii.unhexlify('d2e0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_145_shld_m32_r32_imm8():
    """Test: ID_145: shld dword ptr [ebp - 0x10], esi, 1"""
    runner = Runner()
    # Raw: 0fa475f001
    assert runner.run_test_bytes(
        name='ID_145: shld dword ptr [ebp - 0x10], esi, 1',
        code=binascii.unhexlify('0fa475f001'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_154_shld_r32_r32_imm8():
    """Test: ID_154: shld edi, esi, 1"""
    runner = Runner()
    # Raw: 0fa4f701
    assert runner.run_test_bytes(
        name='ID_154: shld edi, esi, 1',
        code=binascii.unhexlify('0fa4f701'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_288_shld_r32_r32_r8():
    """Test: ID_288: shld edi, esi, cl"""
    runner = Runner()
    # Raw: 0fa5f7
    assert runner.run_test_bytes(
        name='ID_288: shld edi, esi, cl',
        code=binascii.unhexlify('0fa5f7'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_229_shr_m32_imm32():
    """Test: ID_229: shr dword ptr [ebp - 0x20], 1"""
    runner = Runner()
    # Raw: d16de0
    assert runner.run_test_bytes(
        name='ID_229: shr dword ptr [ebp - 0x20], 1',
        code=binascii.unhexlify('d16de0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_333_shr_m32_imm8():
    """Test: ID_333: shr dword ptr [ebp - 0x14], 3"""
    runner = Runner()
    # Raw: c16dec03
    assert runner.run_test_bytes(
        name='ID_333: shr dword ptr [ebp - 0x14], 3',
        code=binascii.unhexlify('c16dec03'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_434_shr_m32_r8():
    """Test: ID_434: shr dword ptr [ebp - 0x20], cl"""
    runner = Runner()
    # Raw: d36de0
    assert runner.run_test_bytes(
        name='ID_434: shr dword ptr [ebp - 0x20], cl',
        code=binascii.unhexlify('d36de0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_228_shr_r32_imm32():
    """Test: ID_228: shr ecx, 1"""
    runner = Runner()
    # Raw: d1e9
    assert runner.run_test_bytes(
        name='ID_228: shr ecx, 1',
        code=binascii.unhexlify('d1e9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_72_shr_r32_imm8():
    """Test: ID_72: shr ecx, 3"""
    runner = Runner()
    # Raw: c1e903
    assert runner.run_test_bytes(
        name='ID_72: shr ecx, 3',
        code=binascii.unhexlify('c1e903'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_213_shr_r32_r8():
    """Test: ID_213: shr eax, cl"""
    runner = Runner()
    # Raw: d3e8
    assert runner.run_test_bytes(
        name='ID_213: shr eax, cl',
        code=binascii.unhexlify('d3e8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_197_shr_r8_imm8():
    """Test: ID_197: shr bl, 3"""
    runner = Runner()
    # Raw: c0eb03
    assert runner.run_test_bytes(
        name='ID_197: shr bl, 3',
        code=binascii.unhexlify('c0eb03'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_210_shrd_r32_r32_imm8():
    """Test: ID_210: shrd ecx, eax, 1"""
    runner = Runner()
    # Raw: 0facc101
    assert runner.run_test_bytes(
        name='ID_210: shrd ecx, eax, 1',
        code=binascii.unhexlify('0facc101'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_285_shrd_r32_r32_r8():
    """Test: ID_285: shrd edi, ebx, cl"""
    runner = Runner()
    # Raw: 0faddf
    assert runner.run_test_bytes(
        name='ID_285: shrd edi, ebx, cl',
        code=binascii.unhexlify('0faddf'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_395_shufpd_r128_r128_imm8():
    """Test: ID_395: shufpd xmm3, xmm4, 2"""
    runner = Runner()
    # Raw: 660fc6dc02
    assert runner.run_test_bytes(
        name='ID_395: shufpd xmm3, xmm4, 2',
        code=binascii.unhexlify('660fc6dc02'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_270_shufps_r128_r128_imm8():
    """Test: ID_270: shufps xmm6, xmm5, 0xe4"""
    runner = Runner()
    # Raw: 0fc6f5e4
    assert runner.run_test_bytes(
        name='ID_270: shufps xmm6, xmm5, 0xe4',
        code=binascii.unhexlify('0fc6f5e4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_433_sqrtsd_r128_r128():
    """Test: ID_433: sqrtsd xmm0, xmm0"""
    runner = Runner()
    # Raw: f20f51c0
    assert runner.run_test_bytes(
        name='ID_433: sqrtsd xmm0, xmm0',
        code=binascii.unhexlify('f20f51c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_243_sub_m32_imm32():
    """Test: ID_243: sub dword ptr [ebp - 0x28], 1"""
    runner = Runner()
    # Raw: 836dd801
    assert runner.run_test_bytes(
        name='ID_243: sub dword ptr [ebp - 0x28], 1',
        code=binascii.unhexlify('836dd801'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_141_sub_m32_r32():
    """Test: ID_141: sub dword ptr [edx + 0x78c], ecx"""
    runner = Runner()
    # Raw: 298a8c070000
    assert runner.run_test_bytes(
        name='ID_141: sub dword ptr [edx + 0x78c], ecx',
        code=binascii.unhexlify('298a8c070000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_6_sub_r32_imm32():
    """Test: ID_6: sub esp, 0xc"""
    runner = Runner()
    # Raw: 83ec0c
    assert runner.run_test_bytes(
        name='ID_6: sub esp, 0xc',
        code=binascii.unhexlify('83ec0c'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_114_sub_r32_m32():
    """Test: ID_114: sub eax, dword ptr [ebp - 0x14]"""
    runner = Runner()
    # Raw: 2b45ec
    assert runner.run_test_bytes(
        name='ID_114: sub eax, dword ptr [ebp - 0x14]',
        code=binascii.unhexlify('2b45ec'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_77_sub_r32_r32():
    """Test: ID_77: sub esi, eax"""
    runner = Runner()
    # Raw: 29c6
    assert runner.run_test_bytes(
        name='ID_77: sub esi, eax',
        code=binascii.unhexlify('29c6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_348_sub_r8_m8():
    """Test: ID_348: sub al, byte ptr [ebp - 0x10]"""
    runner = Runner()
    # Raw: 2a45f0
    assert runner.run_test_bytes(
        name='ID_348: sub al, byte ptr [ebp - 0x10]',
        code=binascii.unhexlify('2a45f0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_299_sub_r8_r8():
    """Test: ID_299: sub cl, dl"""
    runner = Runner()
    # Raw: 28d1
    assert runner.run_test_bytes(
        name='ID_299: sub cl, dl',
        code=binascii.unhexlify('28d1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_385_subpd_r128_m128():
    """Test: ID_385: subpd xmm5, xmmword ptr [ecx]"""
    runner = Runner()
    # Raw: 660f5ca900000000
    assert runner.run_test_bytes(
        name='ID_385: subpd xmm5, xmmword ptr [ecx]',
        code=binascii.unhexlify('660f5ca900000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_387_subpd_r128_r128():
    """Test: ID_387: subpd xmm6, xmm5"""
    runner = Runner()
    # Raw: 660f5cf5
    assert runner.run_test_bytes(
        name='ID_387: subpd xmm6, xmm5',
        code=binascii.unhexlify('660f5cf5'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_421_subsd_r128_m64():
    """Test: ID_421: subsd xmm0, qword ptr [edx]"""
    runner = Runner()
    # Raw: f20f5c8200000000
    assert runner.run_test_bytes(
        name='ID_421: subsd xmm0, qword ptr [edx]',
        code=binascii.unhexlify('f20f5c8200000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_247_subsd_r128_r128():
    """Test: ID_247: subsd xmm0, xmm1"""
    runner = Runner()
    # Raw: f20f5cc1
    assert runner.run_test_bytes(
        name='ID_247: subsd xmm0, xmm1',
        code=binascii.unhexlify('f20f5cc1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_249_test_m32_imm32():
    """Test: ID_249: test dword ptr [ecx + 0x10], 0xfffc000"""
    runner = Runner()
    # Raw: f7411000c0ff0f
    assert runner.run_test_bytes(
        name='ID_249: test dword ptr [ecx + 0x10], 0xfffc000',
        code=binascii.unhexlify('f7411000c0ff0f'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_118_test_m32_r32():
    """Test: ID_118: test dword ptr [eax + 0x58], edi"""
    runner = Runner()
    # Raw: 857858
    assert runner.run_test_bytes(
        name='ID_118: test dword ptr [eax + 0x58], edi',
        code=binascii.unhexlify('857858'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_82_test_m8_imm8():
    """Test: ID_82: test byte ptr [ecx + esi*2], 8"""
    runner = Runner()
    # Raw: f6047108
    assert runner.run_test_bytes(
        name='ID_82: test byte ptr [ecx + esi*2], 8',
        code=binascii.unhexlify('f6047108'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_260_test_m8_r8():
    """Test: ID_260: test byte ptr [ebp - 0x14], cl"""
    runner = Runner()
    # Raw: 844dec
    assert runner.run_test_bytes(
        name='ID_260: test byte ptr [ebp - 0x14], cl',
        code=binascii.unhexlify('844dec'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_158_test_r16_r16():
    """Test: ID_158: test cx, cx"""
    runner = Runner()
    # Raw: 6685c9
    assert runner.run_test_bytes(
        name='ID_158: test cx, cx',
        code=binascii.unhexlify('6685c9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_163_test_r32_imm32():
    """Test: ID_163: test edx, 0x200"""
    runner = Runner()
    # Raw: f7c200020000
    assert runner.run_test_bytes(
        name='ID_163: test edx, 0x200',
        code=binascii.unhexlify('f7c200020000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_13_test_r32_r32():
    """Test: ID_13: test eax, eax"""
    runner = Runner()
    # Raw: 85c0
    assert runner.run_test_bytes(
        name='ID_13: test eax, eax',
        code=binascii.unhexlify('85c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_91_test_r8_imm8():
    """Test: ID_91: test al, 1"""
    runner = Runner()
    # Raw: a801
    assert runner.run_test_bytes(
        name='ID_91: test al, 1',
        code=binascii.unhexlify('a801'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_64_test_r8_r8():
    """Test: ID_64: test dl, dl"""
    runner = Runner()
    # Raw: 84d2
    assert runner.run_test_bytes(
        name='ID_64: test dl, dl',
        code=binascii.unhexlify('84d2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_436_tzcnt_r32_r32():
    """Test: ID_436: tzcnt ecx, esi"""
    runner = Runner()
    # Raw: f30fbcce
    assert runner.run_test_bytes(
        name='ID_436: tzcnt ecx, esi',
        code=binascii.unhexlify('f30fbcce'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_343_ucomisd_r128_m64():
    """Test: ID_343: ucomisd xmm0, qword ptr [ebp - 0x20]"""
    runner = Runner()
    # Raw: 660f2e45e0
    assert runner.run_test_bytes(
        name='ID_343: ucomisd xmm0, qword ptr [ebp - 0x20]',
        code=binascii.unhexlify('660f2e45e0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_326_ucomisd_r128_r128():
    """Test: ID_326: ucomisd xmm1, xmm0"""
    runner = Runner()
    # Raw: 660f2ec8
    assert runner.run_test_bytes(
        name='ID_326: ucomisd xmm1, xmm0',
        code=binascii.unhexlify('660f2ec8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_460_ucomiss_r128_m32():
    """Test: ID_460: ucomiss xmm0, dword ptr [ebp - 0x20]"""
    runner = Runner()
    # Raw: 0f2e45e0
    assert runner.run_test_bytes(
        name='ID_460: ucomiss xmm0, dword ptr [ebp - 0x20]',
        code=binascii.unhexlify('0f2e45e0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_459_ucomiss_r128_r128():
    """Test: ID_459: ucomiss xmm0, xmm1"""
    runner = Runner()
    # Raw: 0f2ec1
    assert runner.run_test_bytes(
        name='ID_459: ucomiss xmm0, xmm1',
        code=binascii.unhexlify('0f2ec1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_386_unpckhpd_r128_r128():
    """Test: ID_386: unpckhpd xmm4, xmm5"""
    runner = Runner()
    # Raw: 660f15e5
    assert runner.run_test_bytes(
        name='ID_386: unpckhpd xmm4, xmm5',
        code=binascii.unhexlify('660f15e5'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_493_unpcklpd_r128_m128():
    """Test: ID_493: unpcklpd xmm0, xmmword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 660f1445b8
    assert runner.run_test_bytes(
        name='ID_493: unpcklpd xmm0, xmmword ptr [ebp - 0x48]',
        code=binascii.unhexlify('660f1445b8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_388_unpcklpd_r128_r128():
    """Test: ID_388: unpcklpd xmm4, xmm4"""
    runner = Runner()
    # Raw: 660f14e4
    assert runner.run_test_bytes(
        name='ID_388: unpcklpd xmm4, xmm4',
        code=binascii.unhexlify('660f14e4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_463_unpcklps_r128_m128():
    """Test: ID_463: unpcklps xmm0, xmmword ptr [ebp - 0xd8]"""
    runner = Runner()
    # Raw: 0f148528ffffff
    assert runner.run_test_bytes(
        name='ID_463: unpcklps xmm0, xmmword ptr [ebp - 0xd8]',
        code=binascii.unhexlify('0f148528ffffff'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_410_unpcklps_r128_r128():
    """Test: ID_410: unpcklps xmm0, xmm1"""
    runner = Runner()
    # Raw: 0f14c1
    assert runner.run_test_bytes(
        name='ID_410: unpcklps xmm0, xmm1',
        code=binascii.unhexlify('0f14c1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_406_xchg_m32_r32():
    """Test: ID_406: xchg dword ptr [ebx + 0x44], eax"""
    runner = Runner()
    # Raw: 878344000000
    assert runner.run_test_bytes(
        name='ID_406: xchg dword ptr [ebx + 0x44], eax',
        code=binascii.unhexlify('878344000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_372_xor_m32_imm32():
    """Test: ID_372: xor dword ptr [ebp - 0x20], 0x6e646f6d"""
    runner = Runner()
    # Raw: 8175e06d6f646e
    assert runner.run_test_bytes(
        name='ID_372: xor dword ptr [ebp - 0x20], 0x6e646f6d',
        code=binascii.unhexlify('8175e06d6f646e'),
        initial_regs={},
        expected_regs={}
    )

