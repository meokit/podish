import pytest
from tests.runner import Runner

def run_test(func, check_unicorn=True):
    runner = Runner()
    asm = func.__doc__
    if not asm:
        raise ValueError(f"No docstring found for {func.__name__}")
    expectations = func()
    runner.run_test(
        name=func.__name__,
        asm=asm,
        check_unicorn=check_unicorn,
        **expectations
    )

@pytest.mark.mmx
def case_mmx_basic():
    """
    mov eax, 0x11223344
    mov edx, 0x55667788
    push eax
    push edx
    movq mm0, [esp]
    movq mm1, mm0
    paddb mm0, mm1
    movq [esp], mm0
    pop edx
    pop eax
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x22446688,
            'edx': 0xAACCEE10
        }
    }

@pytest.mark.mmx
def case_mmx_movq2dq():
    """
    mov eax, 0x11223344
    mov edx, 0x55667788
    push eax
    push edx
    movq mm0, [esp]
    movq2dq xmm0, mm0
    movdqu [esp], xmm0
    pop edx
    pop eax
    emms
    hlt
    """
    # xmm0 gets zero-extended, so high 8 bytes are 0
    # movdqu stores 16 bytes. It will overwrite [esp] to [esp+15]
    # [esp] and [esp+4] get the original mm0
    return {
        'expected_regs': {
            'eax': 0x11223344,
            'edx': 0x55667788
        }
    }

def test_mmx_basic(): run_test(case_mmx_basic)
def test_mmx_movq2dq(): run_test(case_mmx_movq2dq)

@pytest.mark.mmx
def case_pshufw_mmx():
    """
    mov dword [0x2000], 0x00010002
    mov dword [0x2004], 0x00030004
    movq mm0, [0x2000]
    pshufw mm1, mm0, 0x1B
    movd eax, mm1
    psrlq mm1, 32
    movd edx, mm1
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x00040003,
            'edx': 0x00020001,
        }
    }

@pytest.mark.mmx
def case_pinsrw_pextrw_mmx():
    """
    xor eax, eax
    movd mm0, eax
    mov eax, 0xAABBCCDD
    pinsrw mm0, eax, 2
    pextrw ebx, mm0, 2
    movd ecx, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'ebx': 0x0000CCDD,
            'ecx': 0x00000000,
            'edx': 0x0000CCDD,
        }
    }

@pytest.mark.mmx
def case_pmovmskb_mmx():
    """
    mov dword [0x2000], 0x01FF7F80
    mov dword [0x2004], 0x00FE55AA
    movq mm0, [0x2000]
    pmovmskb eax, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x55,
        }
    }

@pytest.mark.mmx
def case_pminub_mmx():
    """
    mov dword [0x2000], 0xFA1EC814
    mov dword [0x2004], 0x80630232
    mov dword [0x2010], 0xF01E640A
    mov dword [0x2014], 0x7F580128
    movq mm0, [0x2000]
    pminub mm0, [0x2010]
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0xF01E640A,
            'edx': 0x7F580128,
        }
    }

@pytest.mark.mmx
def case_pmaxub_mmx():
    """
    mov dword [0x2000], 0xFA1EC814
    mov dword [0x2004], 0x80630232
    mov dword [0x2010], 0xF01E640A
    mov dword [0x2014], 0x7F580128
    movq mm0, [0x2010]
    pmaxub mm0, [0x2000]
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0xFA1EC814,
            'edx': 0x80630232,
        }
    }

@pytest.mark.mmx
def case_pavgb_mmx():
    """
    mov dword [0x2000], 0x08070605
    mov dword [0x2004], 0x04030201
    mov dword [0x2010], 0x09080706
    mov dword [0x2014], 0x05040302
    movq mm0, [0x2000]
    pavgb mm0, [0x2010]
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x05040302,
            'edx': 0x09080706,
        }
    }

@pytest.mark.mmx
def case_pavgw_mmx():
    """
    mov dword [0x2000], 0xFFFF0002
    mov dword [0x2004], 0x00050001
    mov dword [0x2010], 0x000003E9
    mov dword [0x2014], 0x7FFF0006
    movq mm0, [0x2000]
    pavgw mm0, [0x2010]
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x00010002,
            'edx': 0x80020006,
        }
    }

@pytest.mark.mmx
def case_pmulhuw_mmx():
    """
    mov dword [0x2000], 0x8000FFFF
    mov dword [0x2004], 0x00011234
    mov dword [0x2010], 0x00020002
    mov dword [0x2014], 0xFFFF0010
    movq mm0, [0x2000]
    pmulhuw mm0, [0x2010]
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x00010001,
            'edx': 0x00000001,
        }
    }

@pytest.mark.mmx
def case_pminsw_mmx():
    """
    mov dword [0x2000], 0x0064FFFF
    mov dword [0x2004], 0x7530B1E0
    mov dword [0x2010], 0x00320001
    mov dword [0x2014], 0x8300D8F0
    movq mm0, [0x2000]
    pminsw mm0, [0x2010]
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x0032FFFF,
            'edx': 0x8300B1E0,
        }
    }

@pytest.mark.mmx
def case_pmaxsw_mmx():
    """
    mov dword [0x2000], 0x0064FFFF
    mov dword [0x2004], 0x7530B1E0
    mov dword [0x2010], 0x00320001
    mov dword [0x2014], 0x8300D8F0
    movq mm0, [0x2000]
    pmaxsw mm0, [0x2010]
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 0x00640001,
            'edx': 0x7530D8F0,
        }
    }

@pytest.mark.mmx
def case_psadbw_mmx():
    """
    mov dword [0x2000], 0x281E140A
    mov dword [0x2004], 0x00000000
    mov dword [0x2010], 0x04030201
    mov dword [0x2014], 0x00000000
    movq mm0, [0x2000]
    psadbw mm0, [0x2010]
    movd eax, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 90,
        }
    }

@pytest.mark.mmx
def case_pmuludq_mmx():
    """
    mov eax, 3
    movd mm0, eax
    mov eax, 4
    movd mm1, eax
    pmuludq mm0, mm1
    movd eax, mm0
    psrlq mm0, 32
    movd edx, mm0
    emms
    hlt
    """
    return {
        'expected_regs': {
            'eax': 12,
            'edx': 0,
        }
    }

@pytest.mark.mmx
def case_movntq_mmx():
    """
    mov dword [0x2000], 0x55667788
    mov dword [0x2004], 0x11223344
    movq mm0, [0x2000]
    movntq [0x2020], mm0
    emms
    hlt
    """
    return {
        'expected_write': {
            0x2020: 0x1122334455667788,
        }
    }

@pytest.mark.mmx
def case_maskmovq_mmx():
    """
    mov dword [0x2000], 0x04030201
    mov dword [0x2004], 0x08070605
    mov dword [0x2010], 0x00FF0080
    mov dword [0x2014], 0x00FF0080
    mov edi, 0x2030
    movq mm0, [0x2000]
    movq mm1, [0x2010]
    maskmovq mm0, mm1
    emms
    hlt
    """
    return {
        'expected_write': {
            0x2030: 0x01,
            0x2032: 0x03,
            0x2034: 0x05,
            0x2036: 0x07,
        }
    }

def test_pshufw_mmx(): run_test(case_pshufw_mmx)
def test_pinsrw_pextrw_mmx(): run_test(case_pinsrw_pextrw_mmx)
def test_pmovmskb_mmx(): run_test(case_pmovmskb_mmx)
def test_pminub_mmx(): run_test(case_pminub_mmx)
def test_pmaxub_mmx(): run_test(case_pmaxub_mmx)
def test_pavgb_mmx(): run_test(case_pavgb_mmx)
def test_pavgw_mmx(): run_test(case_pavgw_mmx)
def test_pmulhuw_mmx(): run_test(case_pmulhuw_mmx)
def test_pminsw_mmx(): run_test(case_pminsw_mmx)
def test_pmaxsw_mmx(): run_test(case_pmaxsw_mmx)
def test_psadbw_mmx(): run_test(case_psadbw_mmx)
def test_pmuludq_mmx(): run_test(case_pmuludq_mmx)
def test_movntq_mmx(): run_test(case_movntq_mmx)
def test_maskmovq_mmx(): run_test(case_maskmovq_mmx)
