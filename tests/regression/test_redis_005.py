# Redis Regression Test Batch 005
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_489_or_m16_imm16():
    """Test: ID_489: or word ptr [eax + 0x41], 0x220"""
    runner = Runner()
    # Raw: 668148412002
    assert runner.run_test_bytes(
        name='ID_489: or word ptr [eax + 0x41], 0x220',
        code=binascii.unhexlify('668148412002'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_122_or_m32_imm32():
    """Test: ID_122: or dword ptr [eax + 0x30064], 6"""
    runner = Runner()
    # Raw: 83886400030006
    assert runner.run_test_bytes(
        name='ID_122: or dword ptr [eax + 0x30064], 6',
        code=binascii.unhexlify('83886400030006'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_103_or_m32_r32():
    """Test: ID_103: or dword ptr [ebp - 0x50], eax"""
    runner = Runner()
    # Raw: 0945b0
    assert runner.run_test_bytes(
        name='ID_103: or dword ptr [ebp - 0x50], eax',
        code=binascii.unhexlify('0945b0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_107_or_m8_imm8():
    """Test: ID_107: or byte ptr [eax + 0x58], 1"""
    runner = Runner()
    # Raw: 80485801
    assert runner.run_test_bytes(
        name='ID_107: or byte ptr [eax + 0x58], 1',
        code=binascii.unhexlify('80485801'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_166_or_m8_r8():
    """Test: ID_166: or byte ptr [eax + esi + 0x30128], dl"""
    runner = Runner()
    # Raw: 08943028010300
    assert runner.run_test_bytes(
        name='ID_166: or byte ptr [eax + esi + 0x30128], dl',
        code=binascii.unhexlify('08943028010300'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_42_or_r32_imm32():
    """Test: ID_42: or esi, 7"""
    runner = Runner()
    # Raw: 83ce07
    assert runner.run_test_bytes(
        name='ID_42: or esi, 7',
        code=binascii.unhexlify('83ce07'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_88_or_r32_m32():
    """Test: ID_88: or eax, dword ptr [ebp - 0x94]"""
    runner = Runner()
    # Raw: 0b856cffffff
    assert runner.run_test_bytes(
        name='ID_88: or eax, dword ptr [ebp - 0x94]',
        code=binascii.unhexlify('0b856cffffff'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_121_or_r32_r32():
    """Test: ID_121: or edi, edx"""
    runner = Runner()
    # Raw: 09d7
    assert runner.run_test_bytes(
        name='ID_121: or edi, edx',
        code=binascii.unhexlify('09d7'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_313_or_r8_imm8():
    """Test: ID_313: or ch, 0x40"""
    runner = Runner()
    # Raw: 80cd40
    assert runner.run_test_bytes(
        name='ID_313: or ch, 0x40',
        code=binascii.unhexlify('80cd40'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_192_or_r8_m8():
    """Test: ID_192: or al, byte ptr [esi + 0x58]"""
    runner = Runner()
    # Raw: 0a4658
    assert runner.run_test_bytes(
        name='ID_192: or al, byte ptr [esi + 0x58]',
        code=binascii.unhexlify('0a4658'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_106_or_r8_r8():
    """Test: ID_106: or al, dl"""
    runner = Runner()
    # Raw: 08d0
    assert runner.run_test_bytes(
        name='ID_106: or al, dl',
        code=binascii.unhexlify('08d0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_246_orpd_r128_r128():
    """Test: ID_246: orpd xmm0, xmm1"""
    runner = Runner()
    # Raw: 660f56c1
    assert runner.run_test_bytes(
        name='ID_246: orpd xmm0, xmm1',
        code=binascii.unhexlify('660f56c1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_364_orps_r128_r128():
    """Test: ID_364: orps xmm7, xmm2"""
    runner = Runner()
    # Raw: 0f56fa
    assert runner.run_test_bytes(
        name='ID_364: orps xmm7, xmm2',
        code=binascii.unhexlify('0f56fa'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_490_packssdw_r128_r128():
    """Test: ID_490: packssdw xmm1, xmm4"""
    runner = Runner()
    # Raw: 660f6bcc
    assert runner.run_test_bytes(
        name='ID_490: packssdw xmm1, xmm4',
        code=binascii.unhexlify('660f6bcc'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_329_packuswb_r128_r128():
    """Test: ID_329: packuswb xmm0, xmm0"""
    runner = Runner()
    # Raw: 660f67c0
    assert runner.run_test_bytes(
        name='ID_329: packuswb xmm0, xmm0',
        code=binascii.unhexlify('660f67c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_374_paddb_r128_r128():
    """Test: ID_374: paddb xmm6, xmm1"""
    runner = Runner()
    # Raw: 660ffcf1
    assert runner.run_test_bytes(
        name='ID_374: paddb xmm6, xmm1',
        code=binascii.unhexlify('660ffcf1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_361_paddd_r128_m128():
    """Test: ID_361: paddd xmm0, xmmword ptr [ebx]"""
    runner = Runner()
    # Raw: 660ffe8300000000
    assert runner.run_test_bytes(
        name='ID_361: paddd xmm0, xmmword ptr [ebx]',
        code=binascii.unhexlify('660ffe8300000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_276_paddd_r128_r128():
    """Test: ID_276: paddd xmm3, xmm2"""
    runner = Runner()
    # Raw: 660ffeda
    assert runner.run_test_bytes(
        name='ID_276: paddd xmm3, xmm2',
        code=binascii.unhexlify('660ffeda'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_280_paddq_r128_r128():
    """Test: ID_280: paddq xmm0, xmm1"""
    runner = Runner()
    # Raw: 660fd4c1
    assert runner.run_test_bytes(
        name='ID_280: paddq xmm0, xmm1',
        code=binascii.unhexlify('660fd4c1'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_282_pand_r128_m128():
    """Test: ID_282: pand xmm6, xmmword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 660fdb75b8
    assert runner.run_test_bytes(
        name='ID_282: pand xmm6, xmmword ptr [ebp - 0x48]',
        code=binascii.unhexlify('660fdb75b8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_274_pand_r128_r128():
    """Test: ID_274: pand xmm2, xmm0"""
    runner = Runner()
    # Raw: 660fdbd0
    assert runner.run_test_bytes(
        name='ID_274: pand xmm2, xmm0',
        code=binascii.unhexlify('660fdbd0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_480_pandn_r128_m128():
    """Test: ID_480: pandn xmm0, xmmword ptr [ebx]"""
    runner = Runner()
    # Raw: 660fdf8300000000
    assert runner.run_test_bytes(
        name='ID_480: pandn xmm0, xmmword ptr [ebx]',
        code=binascii.unhexlify('660fdf8300000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_332_pandn_r128_r128():
    """Test: ID_332: pandn xmm5, xmm4"""
    runner = Runner()
    # Raw: 660fdfec
    assert runner.run_test_bytes(
        name='ID_332: pandn xmm5, xmm4',
        code=binascii.unhexlify('660fdfec'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_451_pcmpeqb_r128_m128():
    """Test: ID_451: pcmpeqb xmm0, xmmword ptr [ebp - 0x98]"""
    runner = Runner()
    # Raw: 660f748568ffffff
    assert runner.run_test_bytes(
        name='ID_451: pcmpeqb xmm0, xmmword ptr [ebp - 0x98]',
        code=binascii.unhexlify('660f748568ffffff'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_376_pcmpeqb_r128_r128():
    """Test: ID_376: pcmpeqb xmm4, xmm6"""
    runner = Runner()
    # Raw: 660f74e6
    assert runner.run_test_bytes(
        name='ID_376: pcmpeqb xmm4, xmm6',
        code=binascii.unhexlify('660f74e6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_295_pcmpeqd_r128_r128():
    """Test: ID_295: pcmpeqd xmm0, xmm0"""
    runner = Runner()
    # Raw: 660f76c0
    assert runner.run_test_bytes(
        name='ID_295: pcmpeqd xmm0, xmm0',
        code=binascii.unhexlify('660f76c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_484_pcmpgtd_r128_r128():
    """Test: ID_484: pcmpgtd xmm1, xmm4"""
    runner = Runner()
    # Raw: 660f66cc
    assert runner.run_test_bytes(
        name='ID_484: pcmpgtd xmm1, xmm4',
        code=binascii.unhexlify('660f66cc'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_466_pextrw_r32_r128_imm8():
    """Test: ID_466: pextrw edx, xmm3, 2"""
    runner = Runner()
    # Raw: 660fc5d302
    assert runner.run_test_bytes(
        name='ID_466: pextrw edx, xmm3, 2',
        code=binascii.unhexlify('660fc5d302'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_413_pinsrw_r128_r32_imm8():
    """Test: ID_413: pinsrw xmm2, eax, 2"""
    runner = Runner()
    # Raw: 660fc4d002
    assert runner.run_test_bytes(
        name='ID_413: pinsrw xmm2, eax, 2',
        code=binascii.unhexlify('660fc4d002'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_375_pmaxub_r128_r128():
    """Test: ID_375: pmaxub xmm4, xmm2"""
    runner = Runner()
    # Raw: 660fdee2
    assert runner.run_test_bytes(
        name='ID_375: pmaxub xmm4, xmm2',
        code=binascii.unhexlify('660fdee2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_452_pmovmskb_r32_r128():
    """Test: ID_452: pmovmskb ecx, xmm1"""
    runner = Runner()
    # Raw: 660fd7c9
    assert runner.run_test_bytes(
        name='ID_452: pmovmskb ecx, xmm1',
        code=binascii.unhexlify('660fd7c9'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_278_pmuludq_r128_r128():
    """Test: ID_278: pmuludq xmm1, xmm2"""
    runner = Runner()
    # Raw: 660ff4ca
    assert runner.run_test_bytes(
        name='ID_278: pmuludq xmm1, xmm2',
        code=binascii.unhexlify('660ff4ca'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_8_pop_r32():
    """Test: ID_8: pop ebx"""
    runner = Runner()
    # Raw: 5b
    assert runner.run_test_bytes(
        name='ID_8: pop ebx',
        code=binascii.unhexlify('5b'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_465_por_r128_m128():
    """Test: ID_465: por xmm1, xmmword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 660feb4db8
    assert runner.run_test_bytes(
        name='ID_465: por xmm1, xmmword ptr [ebp - 0x48]',
        code=binascii.unhexlify('660feb4db8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_305_por_r128_r128():
    """Test: ID_305: por xmm0, xmm2"""
    runner = Runner()
    # Raw: 660febc2
    assert runner.run_test_bytes(
        name='ID_305: por xmm0, xmm2',
        code=binascii.unhexlify('660febc2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_268_pshufd_r128_r128_imm8():
    """Test: ID_268: pshufd xmm7, xmm5, 0xff"""
    runner = Runner()
    # Raw: 660f70fdff
    assert runner.run_test_bytes(
        name='ID_268: pshufd xmm7, xmm5, 0xff',
        code=binascii.unhexlify('660f70fdff'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_330_pshufhw_r128_r128_imm8():
    """Test: ID_330: pshufhw xmm0, xmm0, 0x1b"""
    runner = Runner()
    # Raw: f30f70c01b
    assert runner.run_test_bytes(
        name='ID_330: pshufhw xmm0, xmm0, 0x1b',
        code=binascii.unhexlify('f30f70c01b'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_328_pshuflw_r128_r128_imm8():
    """Test: ID_328: pshuflw xmm0, xmm0, 0x1b"""
    runner = Runner()
    # Raw: f20f70c01b
    assert runner.run_test_bytes(
        name='ID_328: pshuflw xmm0, xmm0, 0x1b',
        code=binascii.unhexlify('f20f70c01b'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_359_pslld_r128_imm8():
    """Test: ID_359: pslld xmm0, 0x1f"""
    runner = Runner()
    # Raw: 660f72f01f
    assert runner.run_test_bytes(
        name='ID_359: pslld xmm0, 0x1f',
        code=binascii.unhexlify('660f72f01f'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_455_pslldq_r128_imm8():
    """Test: ID_455: pslldq xmm0, 8"""
    runner = Runner()
    # Raw: 660f73f808
    assert runner.run_test_bytes(
        name='ID_455: pslldq xmm0, 8',
        code=binascii.unhexlify('660f73f808'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_377_psllq_r128_imm8():
    """Test: ID_377: psllq xmm7, 0x30"""
    runner = Runner()
    # Raw: 660f73f730
    assert runner.run_test_bytes(
        name='ID_377: psllq xmm7, 0x30',
        code=binascii.unhexlify('660f73f730'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_304_psrad_r128_imm8():
    """Test: ID_304: psrad xmm2, 0x18"""
    runner = Runner()
    # Raw: 660f72e218
    assert runner.run_test_bytes(
        name='ID_304: psrad xmm2, 0x18',
        code=binascii.unhexlify('660f72e218'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_273_psrld_r128_imm8():
    """Test: ID_273: psrld xmm2, 1"""
    runner = Runner()
    # Raw: 660f72d201
    assert runner.run_test_bytes(
        name='ID_273: psrld xmm2, 1',
        code=binascii.unhexlify('660f72d201'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_365_psrlq_r128_imm8():
    """Test: ID_365: psrlq xmm7, 1"""
    runner = Runner()
    # Raw: 660f73d701
    assert runner.run_test_bytes(
        name='ID_365: psrlq xmm7, 1',
        code=binascii.unhexlify('660f73d701'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_275_psubd_r128_r128():
    """Test: ID_275: psubd xmm3, xmm2"""
    runner = Runner()
    # Raw: 660ffada
    assert runner.run_test_bytes(
        name='ID_275: psubd xmm3, xmm2',
        code=binascii.unhexlify('660ffada'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_444_punpckhbw_r128_r128():
    """Test: ID_444: punpckhbw xmm3, xmm0"""
    runner = Runner()
    # Raw: 660f68d8
    assert runner.run_test_bytes(
        name='ID_444: punpckhbw xmm3, xmm0',
        code=binascii.unhexlify('660f68d8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_269_punpckhdq_r128_r128():
    """Test: ID_269: punpckhdq xmm2, xmm6"""
    runner = Runner()
    # Raw: 660f6ad6
    assert runner.run_test_bytes(
        name='ID_269: punpckhdq xmm2, xmm6',
        code=binascii.unhexlify('660f6ad6'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_302_punpcklbw_r128_r128():
    """Test: ID_302: punpcklbw xmm2, xmm2"""
    runner = Runner()
    # Raw: 660f60d2
    assert runner.run_test_bytes(
        name='ID_302: punpcklbw xmm2, xmm2',
        code=binascii.unhexlify('660f60d2'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_384_punpckldq_r128_m128():
    """Test: ID_384: punpckldq xmm5, xmmword ptr [ecx]"""
    runner = Runner()
    # Raw: 660f62a900000000
    assert runner.run_test_bytes(
        name='ID_384: punpckldq xmm5, xmmword ptr [ecx]',
        code=binascii.unhexlify('660f62a900000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_263_punpckldq_r128_r128():
    """Test: ID_263: punpckldq xmm0, xmm1"""
    runner = Runner()
    # Raw: 660f62c1
    assert runner.run_test_bytes(
        name='ID_263: punpckldq xmm0, xmm1',
        code=binascii.unhexlify('660f62c1'),
        initial_regs={},
        expected_regs={}
    )

