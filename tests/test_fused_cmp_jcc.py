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
def case_cmp_reg_imm8_je_taken():
    """
    mov eax, 5
    cmp eax, byte 5
    je short .taken
    mov ebx, 0
    jmp short .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EBX": 1,
        }
    }


@pytest.mark.i686
def case_cmp_reg_imm8_jne_taken():
    """
    mov eax, 5
    cmp eax, byte 6
    jne short .taken
    mov ebx, 0
    jmp short .done
.taken:
    mov ebx, 2
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EBX": 2,
        }
    }


@pytest.mark.i686
def case_cmp_mem_disp8_imm8_je_taken():
    """
    mov ebx, 0x2000
    mov dword [ebx+4], 0x44
    cmp dword [ebx+4], byte 0x44
    je short .taken
    mov eax, 0
    jmp short .done
.taken:
    mov eax, 7
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 7,
        },
        "expected_read": {
            0x2004: 0x44,
        },
    }


@pytest.mark.i686
def case_cmp_esp_sib_still_works_unfused():
    """
    mov esp, 0x8000
    push dword 0x33
    cmp dword [esp], byte 0x33
    je short .taken
    mov eax, 0
    jmp short .done
.taken:
    mov eax, 9
.done:
    add esp, 4
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 9,
            "ESP": 0x8000,
        },
        "expected_read": {
            0x7FFC: 0x33,
        },
    }


def test_cmp_reg_imm8_je_taken():
    run_test(case_cmp_reg_imm8_je_taken)


def test_cmp_reg_imm8_jne_taken():
    run_test(case_cmp_reg_imm8_jne_taken)


def test_cmp_mem_disp8_imm8_je_taken():
    run_test(case_cmp_mem_disp8_imm8_je_taken)


def test_cmp_esp_sib_still_works_unfused():
    run_test(case_cmp_esp_sib_still_works_unfused)
