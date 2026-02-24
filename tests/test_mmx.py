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
