import pytest

from tests.runner import Runner


def run_test(func, check_unicorn=True):
    runner = Runner()
    asm = func.__doc__
    if not asm:
        raise ValueError(f"No docstring found for {func.__name__}")
    expectations = func()
    runner.run_test(name=func.__name__, asm=asm, check_unicorn=check_unicorn, **expectations)


@pytest.mark.i686
def case_push_pop_reg32():
    """
    mov esp, 0x8000
    mov eax, 0x11223344
    push eax
    mov eax, 0
    pop ebx
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 0,
            "EBX": 0x11223344,
            "ESP": 0x8000,
        },
        "expected_write": {
            0x7FFC: 0x11223344,
        },
        "expected_read": {
            0x7FFC: 0x11223344,
        },
    }


@pytest.mark.i686
def case_push_imm8_imm32():
    """
    mov esp, 0x8000
    push byte -1
    push dword 0x12345678
    pop eax
    pop ebx
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 0x12345678,
            "EBX": 0xFFFFFFFF,
            "ESP": 0x8000,
        },
        "expected_write": {
            0x7FFC: 0xFFFFFFFF,
            0x7FF8: 0x12345678,
        },
        "expected_read": {
            0x7FF8: 0x12345678,
            0x7FFC: 0xFFFFFFFF,
        },
    }


@pytest.mark.i686
def case_ret_imm16_discards_args():
    """
    mov esp, 0x8000
    push dword 0xAAAA5555
    push dword 0xDEADBEEF
    call .subroutine
    mov ebx, esp
    hlt

.subroutine:
    mov eax, [esp+4]
    mov ecx, [esp+8]
    ret 8
    """
    return {
        "expected_regs": {
            "EAX": 0xDEADBEEF,
            "ECX": 0xAAAA5555,
            "EBX": 0x8000,
            "ESP": 0x8000,
        },
    }


@pytest.mark.i686
def case_pushfd_popfd_roundtrip():
    """
    stc
    std
    pushfd
    clc
    cld
    popfd
    hlt
    """
    return {
        "expected_eflags": 0x603,
        "check_eflags_mask": 0x401,
    }


@pytest.mark.i686
def case_push_pop_reg16():
    """
    mov esp, 0x8000
    mov eax, 0xAAAA1234
    push ax
    mov ebx, 0xBBBB0000
    pop bx
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 0xAAAA1234,
            "EBX": 0xBBBB1234,
            "ESP": 0x8000,
        },
        "expected_write": {
            0x7FFE: 0x1234,
        },
        "expected_read": {
            0x7FFE: 0x1234,
        },
    }


def test_push_pop_reg32():
    run_test(case_push_pop_reg32)


def test_push_imm8_imm32():
    run_test(case_push_imm8_imm32)


def test_ret_imm16_discards_args():
    run_test(case_ret_imm16_discards_args)


def test_pushfd_popfd_roundtrip():
    run_test(case_pushfd_popfd_roundtrip)


def test_push_pop_reg16():
    run_test(case_push_pop_reg16)
