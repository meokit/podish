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


@pytest.mark.i686
def case_cmp_reg_imm8_je_rel32_taken():
    """
    mov eax, 5
    cmp eax, byte 5
    je near .taken
    mov ebx, 0
    jmp short .done
.taken:
    mov ebx, 3
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EBX": 3,
        }
    }


@pytest.mark.i686
def case_test_reg_reg_jne_rel8_taken():
    """
    mov eax, 1
    mov ebx, 1
    test eax, ebx
    jne short .taken
    mov ecx, 0
    jmp short .done
.taken:
    mov ecx, 4
.done:
    hlt
    """
    return {
        "expected_regs": {
            "ECX": 4,
        }
    }


@pytest.mark.i686
def case_test_mem_reg_je_rel32_taken():
    """
    mov ebx, 0x2000
    mov dword [ebx+8], 0
    mov ecx, 1
    test dword [ebx+8], ecx
    je near .taken
    mov eax, 0
    jmp short .done
.taken:
    mov eax, 8
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 8,
        },
        "expected_read": {
            0x2008: 0,
        },
    }


@pytest.mark.i686
def case_cmp_evgv_mem_reg_jne_rel32_taken():
    """
    mov ebx, 0x2000
    mov dword [ebx+12], 0x44
    mov ecx, 0x45
    cmp dword [ebx+12], ecx
    jne near .taken
    mov eax, 0
    jmp short .done
.taken:
    mov eax, 0x11
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 0x11,
        },
        "expected_read": {
            0x200C: 0x44,
        },
    }


@pytest.mark.i686
def case_cmp_gvev_reg_mem_je_rel8_taken():
    """
    mov ebx, 0x2000
    mov dword [ebx+16], 0x66
    mov ecx, 0x66
    cmp ecx, dword [ebx+16]
    je short .taken
    mov eax, 0
    jmp short .done
.taken:
    mov eax, 0x12
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 0x12,
        },
        "expected_read": {
            0x2010: 0x66,
        },
    }


@pytest.mark.i686
def case_test_reg_reg_jne_rel32_loop_target():
    """
    mov eax, 3
    xor ecx, ecx
.loop:
    test eax, eax
    jne near .taken
    jmp short .done
.taken:
    inc ecx
    dec eax
    jmp short .loop
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EAX": 0,
            "ECX": 3,
        }
    }


@pytest.mark.i686
def case_cmp_ax_imm8_opsize_still_works_unfused():
    """
    mov eax, 5
    cmp ax, byte 5
    je short .taken
    mov ebx, 0
    jmp short .done
.taken:
    mov ebx, 0x55
.done:
    hlt
    """
    return {
        "expected_regs": {
            "EBX": 0x55,
        }
    }


def test_cmp_reg_imm8_je_taken():
    run_test(case_cmp_reg_imm8_je_taken)


def test_cmp_reg_imm8_jne_taken():
    run_test(case_cmp_reg_imm8_jne_taken)


def test_cmp_mem_disp8_imm8_je_taken():
    run_test(case_cmp_mem_disp8_imm8_je_taken)


def test_cmp_esp_sib_still_works_unfused():
    run_test(case_cmp_esp_sib_still_works_unfused)


def test_cmp_reg_imm8_je_rel32_taken():
    run_test(case_cmp_reg_imm8_je_rel32_taken)


def test_test_reg_reg_jne_rel8_taken():
    run_test(case_test_reg_reg_jne_rel8_taken)


def test_test_mem_reg_je_rel32_taken():
    run_test(case_test_mem_reg_je_rel32_taken)


def test_cmp_evgv_mem_reg_jne_rel32_taken():
    run_test(case_cmp_evgv_mem_reg_jne_rel32_taken)


def test_cmp_gvev_reg_mem_je_rel8_taken():
    run_test(case_cmp_gvev_reg_mem_je_rel8_taken)


def test_test_reg_reg_jne_rel32_loop_target():
    run_test(case_test_reg_reg_jne_rel32_loop_target)


def test_cmp_ax_imm8_opsize_still_works_unfused():
    run_test(case_cmp_ax_imm8_opsize_still_works_unfused)
