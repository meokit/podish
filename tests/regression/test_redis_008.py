# Redis Regression Test Batch 008
# Generated automatically. PLEASE EDIT THIS FILE MANUALLY TO FIX TESTS.
from tests.runner import Runner
import binascii
import pytest

@pytest.mark.regression
def test_id_379_xor_m32_r32():
    """Test: ID_379: xor dword ptr [ebp - 0x2c], edx"""
    runner = Runner()
    # Raw: 3155d4
    assert runner.run_test_bytes(
        name='ID_379: xor dword ptr [ebp - 0x2c], edx',
        code=binascii.unhexlify('3155d4'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_331_xor_r16_m16():
    """Test: ID_331: xor ax, word ptr [ecx + ebx*2]"""
    runner = Runner()
    # Raw: 6633845900000000
    assert runner.run_test_bytes(
        name='ID_331: xor ax, word ptr [ecx + ebx*2]',
        code=binascii.unhexlify('6633845900000000'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_232_xor_r32_imm32():
    """Test: ID_232: xor esi, 3"""
    runner = Runner()
    # Raw: 83f603
    assert runner.run_test_bytes(
        name='ID_232: xor esi, 3',
        code=binascii.unhexlify('83f603'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_151_xor_r32_m32():
    """Test: ID_151: xor ecx, dword ptr [ebp - 0x24]"""
    runner = Runner()
    # Raw: 334ddc
    assert runner.run_test_bytes(
        name='ID_151: xor ecx, dword ptr [ebp - 0x24]',
        code=binascii.unhexlify('334ddc'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_23_xor_r32_r32():
    """Test: ID_23: xor edi, edi"""
    runner = Runner()
    # Raw: 31ff
    assert runner.run_test_bytes(
        name='ID_23: xor edi, edi',
        code=binascii.unhexlify('31ff'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_217_xor_r8_imm8():
    """Test: ID_217: xor al, 1"""
    runner = Runner()
    # Raw: 3401
    assert runner.run_test_bytes(
        name='ID_217: xor al, 1',
        code=binascii.unhexlify('3401'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_293_xor_r8_m8():
    """Test: ID_293: xor cl, byte ptr [ebp - 0x48]"""
    runner = Runner()
    # Raw: 324db8
    assert runner.run_test_bytes(
        name='ID_293: xor cl, byte ptr [ebp - 0x48]',
        code=binascii.unhexlify('324db8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_294_xor_r8_r8():
    """Test: ID_294: xor al, cl"""
    runner = Runner()
    # Raw: 30c8
    assert runner.run_test_bytes(
        name='ID_294: xor al, cl',
        code=binascii.unhexlify('30c8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_244_xorpd_r128_r128():
    """Test: ID_244: xorpd xmm0, xmm0"""
    runner = Runner()
    # Raw: 660f57c0
    assert runner.run_test_bytes(
        name='ID_244: xorpd xmm0, xmm0',
        code=binascii.unhexlify('660f57c0'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_237_xorps_r128_m128():
    """Test: ID_237: xorps xmm0, xmmword ptr [ebp - 0x28]"""
    runner = Runner()
    # Raw: 0f5745d8
    assert runner.run_test_bytes(
        name='ID_237: xorps xmm0, xmmword ptr [ebp - 0x28]',
        code=binascii.unhexlify('0f5745d8'),
        initial_regs={},
        expected_regs={}
    )

@pytest.mark.regression
def test_id_109_xorps_r128_r128():
    """Test: ID_109: xorps xmm0, xmm0"""
    runner = Runner()
    # Raw: 0f57c0
    assert runner.run_test_bytes(
        name='ID_109: xorps xmm0, xmm0',
        code=binascii.unhexlify('0f57c0'),
        initial_regs={},
        expected_regs={}
    )

