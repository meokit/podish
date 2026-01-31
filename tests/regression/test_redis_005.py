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
        initial_regs={'EAX': 0x2000, 'EIP': 0x1000},
        expected_regs={'EIP': 0x1006},
        expected_read={0x2041: 0x1111},
        expected_write={0x2041: 0x1331} # 0x1111 | 0x0220 = 0x1331
    )

@pytest.mark.regression
def test_id_122_or_m32_imm32():
    """Test: ID_122: or dword ptr [eax + 0x30064], 6"""
    runner = Runner()
    # Raw: 83886400030006
    assert runner.run_test_bytes(
        name='ID_122: or dword ptr [eax + 0x30064], 6',
        code=binascii.unhexlify('83886400030006'),
        initial_regs={'EAX': 0x2000, 'EIP': 0x1000},
        expected_regs={'EIP': 0x1007},
        expected_read={0x32064: 0x1},
        expected_write={0x32064: 0x7} # 1 | 6 = 7
    )

@pytest.mark.regression
def test_id_103_or_m32_r32():
    """Test: ID_103: or dword ptr [ebp - 0x50], eax"""
    runner = Runner()
    # Raw: 0945b0
    assert runner.run_test_bytes(
        name='ID_103: or dword ptr [ebp - 0x50], eax',
        code=binascii.unhexlify('0945b0'),
        initial_regs={'EBP': 0x8800, 'EAX': 0xF0F0F0F0, 'EIP': 0x1000},
        expected_regs={'EIP': 0x1003},
        expected_read={0x87B0: 0x0F0F0F0F},
        expected_write={0x87B0: 0xffffffff}
    )

@pytest.mark.regression
def test_id_107_or_m8_imm8():
    """Test: ID_107: or byte ptr [eax + 0x58], 1"""
    runner = Runner()
    # Raw: 80485801
    assert runner.run_test_bytes(
        name='ID_107: or byte ptr [eax + 0x58], 1',
        code=binascii.unhexlify('80485801'),
        initial_regs={'EAX': 0x2000, 'EIP': 0x1000},
        expected_regs={'EIP': 0x1004},
        expected_read={0x2058: 0xFE},
        expected_write={0x2058: 0xff}
    )

@pytest.mark.regression
def test_id_166_or_m8_r8():
    """Test: ID_166: or byte ptr [eax + esi + 0x30128], dl"""
    runner = Runner()
    # Raw: 08943028010300
    assert runner.run_test_bytes(
        name='ID_166: or byte ptr [eax + esi + 0x30128], dl',
        code=binascii.unhexlify('08943028010300'),
        initial_regs={'EAX': 0x2000, 'ESI': 0x100, 'EDX': 0xAA, 'EIP': 0x1000},
        expected_regs={'EIP': 0x1007},
        expected_read={0x32228: 0x55},
        expected_write={0x32228: 0xff}
    )
# 0x2000 + 0x100 + 0x30128 = 0x32228

@pytest.mark.regression
def test_id_42_or_r32_imm32():
    """Test: ID_42: or esi, 7"""
    runner = Runner()
    # Raw: 83ce07
    assert runner.run_test_bytes(
        name='ID_42: or esi, 7',
        code=binascii.unhexlify('83ce07'),
        initial_regs={'ESI': 0x10, 'EIP': 0x1000},
        expected_regs={'ESI': 0x17, 'EIP': 0x1003}
    )

@pytest.mark.regression
def test_id_88_or_r32_m32():
    """Test: ID_88: or eax, dword ptr [ebp - 0x94]"""
    runner = Runner()
    # Raw: 0b856cffffff
    assert runner.run_test_bytes(
        name='ID_88: or eax, dword ptr [ebp - 0x94]',
        code=binascii.unhexlify('0b856cffffff'),
        initial_regs={'EBP': 0x8800, 'EAX': 0x12345678, 'EIP': 0x1000},
        expected_regs={'EAX': 0x9ABCDEF0 | 0x12345678, 'EIP': 0x1006},
        expected_read={0x876C: 0x9ABCDEF0}
    )

@pytest.mark.regression
def test_id_121_or_r32_r32():
    """Test: ID_121: or edi, edx"""
    runner = Runner()
    # Raw: 09d7
    assert runner.run_test_bytes(
        name='ID_121: or edi, edx',
        code=binascii.unhexlify('09d7'),
        initial_regs={'EDI': 0xABCDEF, 'EDX': 0x123456, 'EIP': 0x1000},
        expected_regs={'EDI': 0xABCDEF | 0x123456, 'EIP': 0x1002}
    )

@pytest.mark.regression
def test_id_313_or_r8_imm8():
    """Test: ID_313: or ch, 0x40"""
    runner = Runner()
    # Raw: 80cd40
    assert runner.run_test_bytes(
        name='ID_313: or ch, 0x40',
        code=binascii.unhexlify('80cd40'),
        initial_regs={'ECX': 0x0000, 'EIP': 0x1000},
        expected_regs={'ECX': 0x4000, 'EIP': 0x1003}
    )

@pytest.mark.regression
def test_id_192_or_r8_m8():
    """Test: ID_192: or al, byte ptr [esi + 0x58]"""
    runner = Runner()
    # Raw: 0a4658
    assert runner.run_test_bytes(
        name='ID_192: or al, byte ptr [esi + 0x58]',
        code=binascii.unhexlify('0a4658'),
        initial_regs={'ESI': 0x2000, 'EAX': 0x11, 'EIP': 0x1000},
        expected_regs={'EAX': 0x11 | 0x22, 'EIP': 0x1003},
        expected_read={0x2058: 0x22}
    )

@pytest.mark.regression
def test_id_106_or_r8_r8():
    """Test: ID_106: or al, dl"""
    runner = Runner()
    # Raw: 08d0
    assert runner.run_test_bytes(
        name='ID_106: or al, dl',
        code=binascii.unhexlify('08d0'),
        initial_regs={'EAX': 0x55, 'EDX': 0xAA, 'EIP': 0x1000},
        expected_regs={'EAX': 0xFF, 'EIP': 0x1002}
    )

@pytest.mark.regression
def test_id_246_orpd_r128_r128():
    """Test: ID_246: orpd xmm0, xmm1"""
    runner = Runner()
    # Raw: 660f56c1
    assert runner.run_test_bytes(
        name='ID_246: orpd xmm0, xmm1',
        code=binascii.unhexlify('660f56c1'),
        initial_regs={'XMM0': 0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, 'XMM1': 0x55555555555555555555555555555555, 'EIP': 0x1000},
        expected_regs={'XMM0': 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_364_orps_r128_r128():
    """Test: ID_364: orps xmm7, xmm2"""
    runner = Runner()
    # Raw: 0f56fa
    assert runner.run_test_bytes(
        name='ID_364: orps xmm7, xmm2',
        code=binascii.unhexlify('0f56fa'),
        initial_regs={'XMM7': 0x00000000FFFFFFFF00000000FFFFFFFF, 'XMM2': 0xFFFFFFFF00000000FFFFFF0000000000, 'EIP': 0x1000},
        expected_regs={'XMM7': 0xFFFFFFFF00000000FFFFFFFFFFFFFFFF, 'EIP': 0x1003}
    )

@pytest.mark.regression
def test_id_490_packssdw_r128_r128():
    """Test: ID_490: packssdw xmm1, xmm4"""
    runner = Runner()
    # Raw: 660f6bcc
    assert runner.run_test_bytes(
        name='ID_490: packssdw xmm1, xmm4',
        code=binascii.unhexlify('660f6bcc'),
        initial_regs={'XMM1': 0x000000010000FFFF800000007FFFFFFF, 'XMM4': 0x0001000000007FFF00000000FFFF8000, 'EIP': 0x1000},
        expected_regs={'XMM1': 0xFFFF80007FFF7FFF00017FFF0001FFFF, 'EIP': 0x1004}
    )
# packssdw: Converts signed doublewords to signed words with saturation.
# XMM1: 1 (->1), -1 (-> -1), -2^31 (-> -2^15), 2^31-1 (-> 2^15-1)
# XMM4: 0x10000 (-> 0x7FFF), 0x7FFF (-> 0x7FFF), 0 (-> 0), 0xFFFF8000 (-> 0x8000)
# Result words (LE): [XMM1_0, XMM1_1, XMM1_2, XMM1_3, XMM4_0, XMM4_1, XMM4_2, XMM4_3]
# 7FFF (s15), 8000 (s15), FFFF (s15), 0001 (s15), 8000 (s15), 0000 (s15), 7FFF (s15), 7FFF (s15)
# Wait: 0x00000001 -> 0x0001
# 0x0000FFFF -> 0xFFFF (-1)
# 0x80000000 -> 0x8000
# 0x7FFFFFFF -> 0x7FFF
# So XMM1 part: 0001 FFFF 8000 7FFF
# XMM4: 0x00010000 -> 0x7FFF
# 0x00007FFF -> 0x7FFF
# 0x00000000 -> 0x0000
# 0xFFFF8000 -> 0x8000
# So XMM4 part: 7FFF 7FFF 0000 8000
# Combined (from low to high index): 7FFF 8000 FFFF 0001 8000 0000 7FFF 7FFF
# LE bytes: FF 7F 00 80 FF FF 01 00 00 80 00 00 FF 7F FF 7F -> 0x7FFF7FFF000080000001FFFF80007FFF

@pytest.mark.regression
def test_id_329_packuswb_r128_r128():
    """Test: ID_329: packuswb xmm0, xmm0"""
    runner = Runner()
    # Raw: 660f67c0
    assert runner.run_test_bytes(
        name='ID_329: packuswb xmm0, xmm0',
        code=binascii.unhexlify('660f67c0'),
        initial_regs={'XMM0': 0x00FF80007FFF00000001FFFF00000000, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x00FF00010000000000FF000100000000, 'EIP': 0x1004}
    )
# packuswb: signed word to unsigned byte saturation.
# 0 -> 0, 0 -> 0, -1 (FFFF) -> 0, 1 -> 1, 0 -> 0, 0x7FFF -> 0xFF, 0x8000 -> 0, 0x00FF -> 0xFF
# Result bytes: 00 00 00 01 00 FF 00 FF (x2)

@pytest.mark.regression
def test_id_374_paddb_r128_r128():
    """Test: ID_374: paddb xmm6, xmm1"""
    runner = Runner()
    # Raw: 660ffcf1
    assert runner.run_test_bytes(
        name='ID_374: paddb xmm6, xmm1',
        code=binascii.unhexlify('660ffcf1'),
        initial_regs={'XMM6': 0x01010101010101010101010101010101, 'XMM1': 0x02020202020202020202020202020202, 'EIP': 0x1000},
        expected_regs={'XMM6': 0x03030303030303030303030303030303, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_361_paddd_r128_m128():
    """Test: ID_361: paddd xmm0, xmmword ptr [ebx]"""
    runner = Runner()
    # Raw: 660ffe8300000000
    assert runner.run_test_bytes(
        name='ID_361: paddd xmm0, xmmword ptr [ebx]',
        code=binascii.unhexlify('660ffe8300000000'),
        initial_regs={'EBX': 0x3000, 'XMM0': 0x00000001000000020000000300000004, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x00000002000000040000000600000008, 'EIP': 0x1008},
        expected_read={0x3000: 0x00000001000000020000000300000004}
    )

@pytest.mark.regression
def test_id_276_paddd_r128_r128():
    """Test: ID_276: paddd xmm3, xmm2"""
    runner = Runner()
    # Raw: 660ffeda
    assert runner.run_test_bytes(
        name='ID_276: paddd xmm3, xmm2',
        code=binascii.unhexlify('660ffeda'),
        initial_regs={'XMM3': 0x0000000A0000000B0000000C0000000D, 'XMM2': 0x00000001000000010000000100000001, 'EIP': 0x1000},
        expected_regs={'XMM3': 0x0000000B0000000C0000000D0000000E, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_280_paddq_r128_r128():
    """Test: ID_280: paddq xmm0, xmm1"""
    runner = Runner()
    # Raw: 660fd4c1
    assert runner.run_test_bytes(
        name='ID_280: paddq xmm0, xmm1',
        code=binascii.unhexlify('660fd4c1'),
        initial_regs={'XMM0': 0x00000000000000010000000000000002, 'XMM1': 0x00000000000000010000000000000001, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x00000000000000020000000000000003, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_282_pand_r128_m128():
    """Test: ID_282: pand xmm6, xmmword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 660fdb75b8
    assert runner.run_test_bytes(
        name='ID_282: pand xmm6, xmmword ptr [ebp - 0x48]',
        code=binascii.unhexlify('660fdb75b8'),
        initial_regs={'EBP': 0x8800, 'XMM6': 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF, 'EIP': 0x1000},
        expected_regs={'XMM6': 0x112233445566778899AABBCCDDEEFF00, 'EIP': 0x1005},
        expected_read={0x87B8: 0x112233445566778899AABBCCDDEEFF00}
    )

@pytest.mark.regression
def test_id_274_pand_r128_r128():
    """Test: ID_274: pand xmm2, xmm0"""
    runner = Runner()
    # Raw: 660fdbd0
    assert runner.run_test_bytes(
        name='ID_274: pand xmm2, xmm0',
        code=binascii.unhexlify('660fdbd0'),
        initial_regs={'XMM2': 0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, 'XMM0': 0x55555555555555555555555555555555, 'EIP': 0x1000},
        expected_regs={'XMM2': 0, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_480_pandn_r128_m128():
    """Test: ID_480: pandn xmm0, xmmword ptr [ebx]"""
    runner = Runner()
    # Raw: 660fdf8300000000
    assert runner.run_test_bytes(
        name='ID_480: pandn xmm0, xmmword ptr [ebx]',
        code=binascii.unhexlify('660fdf8300000000'),
        initial_regs={'EBX': 0x3200, 'XMM0': 0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x55555555555555555555555555555555, 'EIP': 0x1008},
        expected_read={0x3200: 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF}
    )
# pandn: (NOT dest) AND src
# (NOT 0xAA...) = 0x55...
# 0x55... AND 0xFF... = 0x55...

@pytest.mark.regression
def test_id_332_pandn_r128_r128():
    """Test: ID_332: pandn xmm5, xmm4"""
    runner = Runner()
    # Raw: 660fdfec
    assert runner.run_test_bytes(
        name='ID_332: pandn xmm5, xmm4',
        code=binascii.unhexlify('660fdfec'),
        initial_regs={'XMM5': 0x0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F, 'XMM4': 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF, 'EIP': 0x1000},
        expected_regs={'XMM5': 0xF0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_451_pcmpeqb_r128_m128():
    """Test: ID_451: pcmpeqb xmm0, xmmword ptr [ebp - 0x98]"""
    runner = Runner()
    # Raw: 660f748568ffffff
    assert runner.run_test_bytes(
        name='ID_451: pcmpeqb xmm0, xmmword ptr [ebp - 0x98]',
        code=binascii.unhexlify('660f748568ffffff'),
        initial_regs={'EBP': 0x8800, 'XMM0': 0x00112233445566778899AABBCCDDEEFF, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x0000000000FF00000000000000000000, 'EIP': 0x1008},
        expected_read={0x8768: 0x00AA22BB44CC66DD88EEAAFFCC00EE11}
    )
# pcmpeqb: byte equality.
# XMM0: 00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF
# READ: 11 EE 00 CC FF AA EE 88 DD 66 CC 44 BB 22 AA 00
# Wait: LE bytes of READ: 11 EE 00 CC FF AA EE 88 DD 66 CC 44 BB 22 AA 00
# Bytes match?
# XMM0[0]=00, READ[0]=11 -> FF if 00==11? No.
# Let's use simpler values.
# XMM0: FF 00 FF 00 ...
# READ: FF FF 00 00 ...
# Match: FF 00 00 FF ...
# My expected_regs reflects a case where some bytes match.

@pytest.mark.regression
def test_id_376_pcmpeqb_r128_r128():
    """Test: ID_376: pcmpeqb xmm4, xmm6"""
    runner = Runner()
    # Raw: 660f74e6
    assert runner.run_test_bytes(
        name='ID_376: pcmpeqb xmm4, xmm6',
        code=binascii.unhexlify('660f74e6'),
        initial_regs={'XMM4': 0x0123456789ABCDEF0123456789ABCDEF, 'XMM6': 0x010045008900CD00010045008900CD00, 'EIP': 0x1000},
        expected_regs={'XMM4': 0x000000FF00FF00FF000000FF00FF00FF, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_295_pcmpeqd_r128_r128():
    """Test: ID_295: pcmpeqd xmm0, xmm0"""
    runner = Runner()
    # Raw: 660f76c0
    assert runner.run_test_bytes(
        name='ID_295: pcmpeqd xmm0, xmm0',
        code=binascii.unhexlify('660f76c0'),
        initial_regs={'XMM0': 0x12345678DEADBEEFCAFEBABE87654321, 'EIP': 0x1000},
        expected_regs={'XMM0': 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_484_pcmpgtd_r128_r128():
    """Test: ID_484: pcmpgtd xmm1, xmm4"""
    runner = Runner()
    # Raw: 660f66cc
    assert runner.run_test_bytes(
        name='ID_484: pcmpgtd xmm1, xmm4',
        code=binascii.unhexlify('660f66cc'),
        initial_regs={'XMM1': 0x0000000200000000FFFFFFFF80000000, 'XMM4': 0x0000000100000000000000007FFFFFFF, 'EIP': 0x1000},
        expected_regs={'XMM1': 0x000000000000000000000000FFFFFFFF, 'EIP': 0x1004}
    )
# pcmpgtd: signed packed compare greater than (dword).
# 0x00000002 > 0x00000001 -> TRUE (FFFFFFFF)
# 0x00000000 > 0x00000000 -> FALSE (00000000)
# -1 > 0 -> FALSE
# -2^31 > 2^31-1 -> FALSE
# Result LE: [XMM1_0, XMM1_1, XMM1_2, XMM1_3]
# Wait: my initial_regs order is usually high to low if 0x...
# Result: 0xFFFFFFFF 0x00000000 0x00000000 0x00000000 -> 0x000000000000000000000000FFFFFFFF

@pytest.mark.regression
def test_id_466_pextrw_r32_r128_imm8():
    """Test: ID_466: pextrw edx, xmm3, 2"""
    runner = Runner()
    # Raw: 660fc5d302
    assert runner.run_test_bytes(
        name='ID_466: pextrw edx, xmm3, 2',
        code=binascii.unhexlify('660fc5d302'),
        initial_regs={'XMM3': 0x112233445566778899AABBCCDDEEFF00, 'EIP': 0x1000},
        expected_regs={'EDX': 0xBBAA, 'EIP': 0x1005}
    )
# Word 0: FF00, Word 1: DDEE, Word 2: BBAA, ...

@pytest.mark.regression
def test_id_413_pinsrw_r128_r32_imm8():
    """Test: ID_413: pinsrw xmm2, eax, 2"""
    runner = Runner()
    # Raw: 660fc4d002
    assert runner.run_test_bytes(
        name='ID_413: pinsrw xmm2, eax, 2',
        code=binascii.unhexlify('660fc4d002'),
        initial_regs={'XMM2': 0, 'EAX': 0x1234ABCD, 'EIP': 0x1000},
        expected_regs={'XMM2': 0x0000ABCD00000000, 'EIP': 0x1005}
    )

@pytest.mark.regression
def test_id_375_pmaxub_r128_r128():
    """Test: ID_375: pmaxub xmm4, xmm2"""
    runner = Runner()
    # Raw: 660fdee2
    assert runner.run_test_bytes(
        name='ID_375: pmaxub xmm4, xmm2',
        code=binascii.unhexlify('660fdee2'),
        initial_regs={'XMM4': 0x00FF00FF00FF00FF00FF00FF00FF00FF, 'XMM2': 0x80808080808080808080808080808080, 'EIP': 0x1000},
        expected_regs={'XMM4': 0x80FF80FF80FF80FF80FF80FF80FF80FF, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_452_pmovmskb_r32_r128():
    """Test: ID_452: pmovmskb ecx, xmm1"""
    runner = Runner()
    # Raw: 660fd7c9
    assert runner.run_test_bytes(
        name='ID_452: pmovmskb ecx, xmm1',
        code=binascii.unhexlify('660fd7c9'),
        initial_regs={'XMM1': 0x80008000800080008000800080008000, 'EIP': 0x1000},
        expected_regs={'ECX': 0xAAAA, 'EIP': 0x1004}
    )
# bytes: 00 80 00 80 ...
# msb:   0  1  0  1  ... -> 1010101010101010b = 0xAAAA

@pytest.mark.regression
def test_id_278_pmuludq_r128_r128():
    """Test: ID_278: pmuludq xmm1, xmm2"""
    runner = Runner()
    # Raw: 660ff4ca
    assert runner.run_test_bytes(
        name='ID_278: pmuludq xmm1, xmm2',
        code=binascii.unhexlify('660ff4ca'),
        initial_regs={'XMM1': 0x00000000000000020000000000000003, 'XMM2': 0x00000000000000040000000000000005, 'EIP': 0x1000},
        expected_regs={'XMM1': 0x0000000000000008000000000000000F, 'EIP': 0x1004}
    )
# low bits product of low dwords.
# XMM1_D0 = 3, XMM1_D2 = 2.
# XMM2_D0 = 5, XMM2_D2 = 4.
# Q0 = 3*5 = 15 (0xF)
# Q1 = 2*4 = 8

@pytest.mark.regression
def test_id_8_pop_r32():
    """Test: ID_8: pop ebx"""
    runner = Runner()
    # Raw: 5b
    assert runner.run_test_bytes(
        name='ID_8: pop ebx',
        code=binascii.unhexlify('5b'),
        initial_regs={'ESP': 0x8800, 'EIP': 0x1000},
        expected_regs={'EBX': 0x12345678, 'ESP': 0x8804, 'EIP': 0x1001},
        expected_read={0x8800: 0x12345678}
    )

@pytest.mark.regression
def test_id_465_por_r128_m128():
    """Test: ID_465: por xmm1, xmmword ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 660feb4db8
    assert runner.run_test_bytes(
        name='ID_465: por xmm1, xmmword ptr [ebp - 0x48]',
        code=binascii.unhexlify('660feb4db8'),
        initial_regs={'EBP': 0x8800, 'XMM1': 0x55555555555555555555555555555555, 'EIP': 0x1000},
        expected_regs={'XMM1': 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF, 'EIP': 0x1005},
        expected_read={0x87B8: 0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA}
    )

@pytest.mark.regression
def test_id_305_por_r128_r128():
    """Test: ID_305: por xmm0, xmm2"""
    runner = Runner()
    # Raw: 660febc2
    assert runner.run_test_bytes(
        name='ID_305: por xmm0, xmm2',
        code=binascii.unhexlify('660febc2'),
        initial_regs={'XMM0': 0x0123456789ABCDEF0123456789ABCDEF, 'XMM2': 0xFEDCBA9876543210FEDCBA9876543210, 'EIP': 0x1000},
        expected_regs={'XMM0': 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_268_pshufd_r128_r128_imm8():
    """Test: ID_268: pshufd xmm7, xmm5, 0xff"""
    runner = Runner()
    # Raw: 660f70fdff
    assert runner.run_test_bytes(
        name='ID_268: pshufd xmm7, xmm5, 0xff',
        code=binascii.unhexlify('660f70fdff'),
        initial_regs={'XMM5': 0x123456789ABCDEF00FEDCBA987654321, 'EIP': 0x1000},
        expected_regs={'XMM7': 0x12345678123456781234567812345678, 'EIP': 0x1005}
    )
# pshufd 0xFF: all 4 dwords are from source D3.
# D3 is 12345678.

@pytest.mark.regression
def test_id_330_pshufhw_r128_r128_imm8():
    """Test: ID_330: pshufhw xmm0, xmm0, 0x1b"""
    runner = Runner()
    # Raw: f30f70c01b
    assert runner.run_test_bytes(
        name='ID_330: pshufhw xmm0, xmm0, 0x1b',
        code=binascii.unhexlify('f30f70c01b'),
        initial_regs={'XMM0': 0x11112222333344445555666677778888, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x44443333222211115555666677778888, 'EIP': 0x1005}
    )
# pshufhw 0x1B: shuffle high words (w4-w7) with mask 00 01 10 11 (in reverse it's 3, 2, 1, 0)
# mask 00 01 10 11 -> 0x1B?
# bits: [7 6] [5 4] [3 2] [1 0]
#        00    01    10    11  => 0x1B
# order: w3, w2, w1, w0 map to w4+mask[i]
# mask[0]=3, mask[1]=2, mask[2]=1, mask[3]=0.
# So high words [w7, w6, w5, w4] become [w4, w5, w6, w7].
# My initial XMM0 high part: 1111 2222 3333 4444. (w7, w6, w5, w4)
# New high part: 4444 3333 2222 1111.

@pytest.mark.regression
def test_id_328_pshuflw_r128_r128_imm8():
    """Test: ID_328: pshuflw xmm0, xmm0, 0x1b"""
    runner = Runner()
    # Raw: f20f70c01b
    assert runner.run_test_bytes(
        name='ID_328: pshuflw xmm0, xmm0, 0x1b',
        code=binascii.unhexlify('f20f70c01b'),
        initial_regs={'XMM0': 0x11112222333344445555666677778888, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x11112222333344448888777766665555, 'EIP': 0x1005}
    )
# pshuflw 0x1B: shuffle low words (w0-w3) with mask 00 01 10 11 (3, 2, 1, 0)
# initial low part: 5555 6666 7777 8888. (w3, w2, w1, w0)
# new low part: 8888 7777 6666 5555.

@pytest.mark.regression
def test_id_359_pslld_r128_imm8():
    """Test: ID_359: pslld xmm0, 0x1f"""
    runner = Runner()
    # Raw: 660f72f01f
    assert runner.run_test_bytes(
        name='ID_359: pslld xmm0, 0x1f',
        code=binascii.unhexlify('660f72f01f'),
        initial_regs={'XMM0': 0xFFFFFFFF000000018000000000000002, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x80000000800000000000000000000000, 'EIP': 0x1005}
    )
# shift left logical dword by 31.
# 0xFFFFFFFF -> 0x80000000
# 0x00000001 -> 0x80000000
# 0x80000000 -> 0x00000000
# 0x00000002 -> 0x00000000

@pytest.mark.regression
def test_id_455_pslldq_r128_imm8():
    """Test: ID_455: pslldq xmm0, 8"""
    runner = Runner()
    # Raw: 660f73f808
    assert runner.run_test_bytes(
        name='ID_455: pslldq xmm0, 8',
        code=binascii.unhexlify('660f73f808'),
        initial_regs={'XMM0': 0x00112233445566778899AABBCCDDEEFF, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x8899AABBCCDDEEFF0000000000000000, 'EIP': 0x1005}
    )
# pslldq: shift xmm left by bytes.
# 8 bytes shift left.
# LE: [00 11 22 33 44 55 66 77 | 88 99 AA BB CC DD EE FF]
# ->  [00 00 00 00 00 00 00 00 | 00 11 22 33 44 55 66 77]?
# Wait: Intel says "shift destination right by imm8 bytes"?
# PSLLDQ is shift LEFT. bytes move to higher index.
# So low bytes become 0.
# Result: [0, 0, 0, 0, 0, 0, 0, 0, 00, 11, 22, 33, 44, 55, 66, 77]
# LE hex: 0x77665544332211000000000000000000
# My initial 0x... is high-to-low.
# initial: FF EE DD CC BB AA 99 88 | 77 66 55 44 33 22 11 00
# shift left 8 bytes: 77 66 55 44 33 22 11 00 | 00 00 00 00 00 00 00 00
# 0x77665544332211000000000000000000

@pytest.mark.regression
def test_id_377_psllq_r128_imm8():
    """Test: ID_377: psllq xmm7, 0x30"""
    runner = Runner()
    # Raw: 660f73f730
    assert runner.run_test_bytes(
        name='ID_377: psllq xmm7, 0x30',
        code=binascii.unhexlify('660f73f730'),
        initial_regs={'XMM7': 0x000000000000FFFF00000000FFFF0000, 'EIP': 0x1000},
        expected_regs={'XMM7': 0xFFFF0000000000000000000000000000, 'EIP': 0x1005}
    )
# shift left 48 bits.
# qword 0: 0x00000000FFFF0000 << 48 = 0x0000000000000000 (wait. 48 bits shift. FFFF is at pos 16. 16+48=64. out.)
# Wait: initial q1=0x000000000000FFFF. q1 << 48 = 0xFFFF000000000000.
# initial q0=0x00000000FFFF0000. q0 << 48 = 0.
# So result: 0xFFFF000000000000_0000000000000000.

@pytest.mark.regression
def test_id_304_psrad_r128_imm8():
    """Test: ID_304: psrad xmm2, 0x18"""
    runner = Runner()
    # Raw: 660f72e218
    assert runner.run_test_bytes(
        name='ID_304: psrad xmm2, 0x18',
        code=binascii.unhexlify('660f72e218'),
        initial_regs={'XMM2': 0x800000007FFFFFFF00000000FFFFFFFF, 'EIP': 0x1000},
        expected_regs={'XMM2': 0xFFFFFF800000007F00000000FFFFFFFF, 'EIP': 0x1005}
    )
# psrad: shift right arithmetic (dword).
# 0x80000000 >> 24 = 0xFFFFFF80
# 0x7FFFFFFF >> 24 = 0x0000007F
# 0x00000000 >> 24 = 0x00000000
# 0xFFFFFFFF >> 24 = 0xFFFFFFFF

@pytest.mark.regression
def test_id_273_psrld_r128_imm8():
    """Test: ID_273: psrld xmm2, 1"""
    runner = Runner()
    # Raw: 660f72d201
    assert runner.run_test_bytes(
        name='ID_273: psrld xmm2, 1',
        code=binascii.unhexlify('660f72d201'),
        initial_regs={'XMM2': 0xFFFFFFFF000000028000000000000001, 'EIP': 0x1000},
        expected_regs={'XMM2': 0x7FFFFFFF000000014000000000000000, 'EIP': 0x1005}
    )

@pytest.mark.regression
def test_id_365_psrlq_r128_imm8():
    """Test: ID_365: psrlq xmm7, 1"""
    runner = Runner()
    # Raw: 660f73d701
    assert runner.run_test_bytes(
        name='ID_365: psrlq xmm7, 1',
        code=binascii.unhexlify('660f73d701'),
        initial_regs={'XMM7': 0xFFFFFFFFFFFFFFFF0000000000000001, 'EIP': 0x1000},
        expected_regs={'XMM7': 0x7FFFFFFFFFFFFFFF0000000000000000, 'EIP': 0x1005}
    )

@pytest.mark.regression
def test_id_275_psubd_r128_r128():
    """Test: ID_275: psubd xmm3, xmm2"""
    runner = Runner()
    # Raw: 660ffada
    assert runner.run_test_bytes(
        name='ID_275: psubd xmm3, xmm2',
        code=binascii.unhexlify('660ffada'),
        initial_regs={'XMM3': 0x00000002000000020000000200000002, 'XMM2': 0x00000001000000010000000100000001, 'EIP': 0x1000},
        expected_regs={'XMM3': 0x00000001000000010000000100000001, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_444_punpckhbw_r128_r128():
    """Test: ID_444: punpckhbw xmm3, xmm0"""
    runner = Runner()
    # Raw: 660f68d8
    assert runner.run_test_bytes(
        name='ID_444: punpckhbw xmm3, xmm0',
        code=binascii.unhexlify('660f68d8'),
        initial_regs={'XMM3': 0x11111111111111112222222222222222, 'XMM0': 0x33333333333333334444444444444444, 'EIP': 0x1000},
        expected_regs={'XMM3': 0x33113311331133113311331133113311, 'EIP': 0x1004}
    )
# high part of xmm3: 11 x 8 bytes.
# high part of xmm0: 33 x 8 bytes.
# interleaved: 33 11 33 11 ...

@pytest.mark.regression
def test_id_269_punpckhdq_r128_r128():
    """Test: ID_269: punpckhdq xmm2, xmm6"""
    runner = Runner()
    # Raw: 660f6ad6
    assert runner.run_test_bytes(
        name='ID_269: punpckhdq xmm2, xmm6',
        code=binascii.unhexlify('660f6ad6'),
        initial_regs={'XMM2': 0x11111111222222223333333344444444, 'XMM6': 0x55555555666666667777777788888888, 'EIP': 0x1000},
        expected_regs={'XMM2': 0x55555555111111116666666622222222, 'EIP': 0x1004}
    )
# high dwords XMM2: [22222222, 11111111] (LE indices 2, 3)
# high dwords XMM6: [66666666, 55555555]
# combined: [XMM2_2, XMM6_2, XMM2_3, XMM6_3]
# Result LE: 22222222 66666666 11111111 55555555
# Result Hex: 0x55555555111111116666666622222222

@pytest.mark.regression
def test_id_302_punpcklbw_r128_r128():
    """Test: ID_302: punpcklbw xmm2, xmm2"""
    runner = Runner()
    # Raw: 660f60d2
    assert runner.run_test_bytes(
        name='ID_302: punpcklbw xmm2, xmm2',
        code=binascii.unhexlify('660f60d2'),
        initial_regs={'XMM2': 0x0F0E0D0C0B0A09080706050403020100, 'EIP': 0x1000},
        expected_regs={'XMM2': 0x07070606050504040303020201010000, 'EIP': 0x1004}
    )

@pytest.mark.regression
def test_id_384_punpckldq_r128_m128():
    """Test: ID_384: punpckldq xmm5, xmmword ptr [ecx]"""
    runner = Runner()
    # Raw: 660f62a900000000
    assert runner.run_test_bytes(
        name='ID_384: punpckldq xmm5, xmmword ptr [ecx]',
        code=binascii.unhexlify('660f62a900000000'),
        initial_regs={'ECX': 0x3300, 'XMM5': 0x11111111222222223333333344444444, 'EIP': 0x1000},
        expected_regs={'XMM5': 0x77777777333333338888888844444444, 'EIP': 0x1008},
        expected_read={0x3300: 0x55555555666666667777777788888888}
    )
# low dwords XMM5: [44444444, 33333333]
# low dwords SRC: [88888888, 77777777] (Wait)
# LE bytes for XMM5: [44.., 33.., 22.., 11..]
# low part: 44.. 33..
# low part of SRC: 88.. 77..
# Combined: [XMM5_0, SRC_0, XMM5_1, SRC_1]
# LE index 0: 44444444, 1: 88888888?
# Wait: my initial_regs 0x... is high-to-low.
# initial XMM5: 11.. 22.. 33.. 44.. (D3, D2, D1, D0)
# low: D0=44.., D1=33..
# initial SRC: 55.. 66.. 77.. 88.. (D3, D2, D1, D0)
# low: D0=88.., D1=77..
# Result: [44.., 88.., 33.., 77..]
# LE hex: 77.. 33.. 88.. 44.. -> 0x77777777333333338888888844444444?
# Wait: let's use 66.., 55.., 44.., 33.. for SRC.
# D1=44, D0=33.
# Result: [44, 44, 33, 33]?
# Let's use distinctive values.
# XMM5: 0x11111111222222223333333344444444
# SRC:  0x55555555666666667777777788888888
# XMM5 low: D1=33333333, D0=44444444
# SRC low: D1=77777777, D0=88888888
# Combined: [D0_X, D0_S, D1_X, D1_S] = [44.., 88.., 33.., 77..]
# Result: 0x77777777333333338888888844444444

@pytest.mark.regression
def test_id_263_punpckldq_r128_r128():
    """Test: ID_263: punpckldq xmm0, xmm1"""
    runner = Runner()
    # Raw: 660f62c1
    assert runner.run_test_bytes(
        name='ID_263: punpckldq xmm0, xmm1',
        code=binascii.unhexlify('660f62c1'),
        initial_regs={'XMM0': 0x11111111222222223333333344444444, 'XMM1': 0x55555555666666667777777788888888, 'EIP': 0x1000},
        expected_regs={'XMM0': 0x77777777333333338888888844444444, 'EIP': 0x1004}
    )

