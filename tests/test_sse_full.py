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

# =============================================================================
# Category 1: Integer SIMD
# =============================================================================

@pytest.mark.sse
def case_paddusb():
    """
    ; PADDUSB xmm0, xmm1 (Saturated Unsigned Add)
    ; xmm0 = [0, 200, 255, 0...]
    ; xmm1 = [10, 100, 10, 0...]
    ; res = [10, 255, 255, 0...]
    mov eax, 0x00FFC800
    movd xmm0, eax
    mov eax, 0x000A640A
    movd xmm1, eax
    paddusb xmm0, xmm1
    hlt
    """
    return {}

@pytest.mark.sse
def case_pmullw():
    """
    ; PMULLW xmm0, xmm1
    mov eax, 0x00020003
    movd xmm0, eax
    mov eax, 0x00040005
    movd xmm1, eax
    pmullw xmm0, xmm1
    hlt
    """
    return {}

@pytest.mark.sse
def case_psadbw():
    """
    ; PSADBW xmm0, xmm1
    pxor xmm0, xmm0
    pxor xmm1, xmm1
    ; xmm0 = [10, 20, 30, 40, ...] (first 8 bytes)
    ; xmm1 = [1, 2, 3, 4, ...] (first 8 bytes)
    ; Sum of abs diff = (9+18+27+36 + 0*4) = 90
    mov eax, 0x281E140A
    movd xmm0, eax
    mov eax, 0x04030201
    movd xmm1, eax
    psadbw xmm0, xmm1
    hlt
    """
    return {}

# =============================================================================
# Category 2: FP Math
# =============================================================================

@pytest.mark.sse
def case_comiss():
    """
    mov eax, 0x3F800000 ; 1.0
    movd xmm0, eax
    mov eax, 0x40000000 ; 2.0
    movd xmm1, eax
    comiss xmm0, xmm1 ; 1.0 < 2.0 -> CF=1, ZF=0, PF=0
    hlt
    """
    return {}

@pytest.mark.sse
def case_rcpps():
    """
    mov eax, 0x40000000 ; 2.0
    movd xmm1, eax
    rcpps xmm0, xmm1
    hlt
    """
    # RCP is approximate, Unicorn and us might differ slightly
    return {}

# =============================================================================
# Category 3: Data Movement
# =============================================================================

@pytest.mark.sse
def case_movmskpd():
    """
    ; Set xmm0 = [-1.0, 1.0] (high bits set for -1.0)
    pcmpeqd xmm0, xmm0 ; all 1s
    pxor xmm1, xmm1
    unpcklpd xmm0, xmm1 ; xmm0 = [all 1s, all 0s]
    movmskpd eax, xmm0 ; Should be 1 (low element has sign bit set)
    hlt
    """
    return {}

@pytest.mark.sse
def case_movntdq():
    """
    pcmpeqd xmm0, xmm0
    mov edi, 0x2000
    movntdq [edi], xmm0
    hlt
    """
    return {
        'expected_write': {0x2000: (1 << 128) - 1}
    }

@pytest.mark.sse
def case_movsldup():
    """
    ; xmm1 = [1.0, 2.0, 3.0, 4.0]
    mov eax, 0x3F800000
    mov [0x2000], eax
    mov eax, 0x40000000
    mov [0x2004], eax
    mov eax, 0x40400000
    mov [0x2008], eax
    mov eax, 0x40800000
    mov [0x200C], eax
    movups xmm1, [0x2000]
    movsldup xmm0, xmm1 ; [1.0, 1.0, 3.0, 3.0]
    hlt
    """
    return {}

# =============================================================================
# Category 4: Conversions
# =============================================================================

@pytest.mark.sse
def case_cvtsd2si():
    """
    mov eax, 0x40000000
    mov [0x2000], eax
    mov eax, 0x00000000
    mov [0x2004], eax
    movsd xmm1, [0x2000] ; 2.0
    cvtsd2si eax, xmm1
    hlt
    """
    return {}

# =============================================================================
# Category 5: State
# =============================================================================

@pytest.mark.sse
def case_ldmxcsr():
    """
    mov eax, 0x1F80 ; Default MXCSR
    mov [0x2000], eax
    ldmxcsr [0x2000]
    stmxcsr [0x2010]
    hlt
    """
    return {
        'expected_write': {0x2010: 0x1F80}
    }

@pytest.mark.sse
def case_fences():
    """
    lfence
    mfence
    sfence
    hlt
    """
    return {}

# =============================================================================
# Runners
# =============================================================================

def test_paddusb(): run_test(case_paddusb)
def test_pmullw(): run_test(case_pmullw)
def test_psadbw(): run_test(case_psadbw)
def test_comiss(): run_test(case_comiss)
def test_rcpps(): run_test(case_rcpps, check_unicorn=False)
def test_movmskpd(): run_test(case_movmskpd)
def test_movntdq(): run_test(case_movntdq)
def test_movsldup(): run_test(case_movsldup)
def test_cvtsd2si(): run_test(case_cvtsd2si)
def test_ldmxcsr(): run_test(case_ldmxcsr)
def test_fences(): run_test(case_fences)
