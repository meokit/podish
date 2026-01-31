
import pytest
from tests.runner import Runner

def run_jmp_test(func):
    """
    Helper to run test using docstring as assembly.
    """
    runner = Runner()
    asm = func.__doc__
    if not asm:
        raise ValueError(f"No docstring found for {func.__name__}")
    
    # Get test expectations from function
    expectations = func()
    
    runner.run_test(
        name=func.__name__,
        asm=asm,
        **expectations
    )

# =============================================================================
# Unconditional Jumps
# =============================================================================

@pytest.mark.jump
def case_jmp_short_forward():
    """
    mov eax, 1
    jmp short .target
    mov eax, 2      ; Should be skipped
.target:
    mov ebx, 3
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 1,
            'EBX': 3
        }
    }

@pytest.mark.jump
def case_jmp_short_backward():
    """
    jmp short .start
    mov eax, 99     ; Should be skipped
.start:
    mov eax, 1
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 1
        }
    }

@pytest.mark.jump
def case_jmp_near_forward():
    """
    mov eax, 1
    jmp .target
    times 100 nop   ; Large gap
.target:
    mov ebx, 3
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 1,
            'EBX': 3
        }
    }

@pytest.mark.jump
def case_jmp_indirect_reg():
    """
    mov edx, .target
    jmp edx
    mov eax, 99     ; Should be skipped
.target:
    mov eax, 42
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 42
        }
    }

# =============================================================================
# Conditional Jumps - All 16 Conditions
# =============================================================================

@pytest.mark.jump
def case_jcc_je_taken():
    """
    ; Set ZF=1
    xor eax, eax
    test eax, eax   ; ZF=1
    je .taken
    mov ebx, 0      ; Should be skipped
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_je_not_taken():
    """
    mov eax, 1
    test eax, eax   ; ZF=0
    je .taken
    mov ebx, 2      ; Should execute
    jmp .done
.taken:
    mov ebx, 99
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 2
        }
    }

@pytest.mark.jump
def case_jcc_jne_taken():
    """
    mov eax, 1
    test eax, eax   ; ZF=0
    jne .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jg_taken():
    """
    ; Greater (signed): ZF=0 and SF=OF
    mov eax, 5
    cmp eax, 3      ; 5 > 3, ZF=0, SF=0, OF=0
    jg .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jl_taken():
    """
    ; Less (signed): SF != OF
    mov eax, 3
    cmp eax, 5      ; 3 < 5, results in SF=1, OF=0
    jl .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jge_taken():
    """
    ; Greater or Equal (signed): SF=OF
    mov eax, 5
    cmp eax, 5      ; 5 >= 5, ZF=1, SF=0, OF=0
    jge .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jle_taken():
    """
    ; Less or Equal (signed): ZF=1 or SF!=OF
    mov eax, 3
    cmp eax, 3      ; 3 <= 3, ZF=1
    jle .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_ja_taken():
    """
    ; Above (unsigned): CF=0 and ZF=0
    mov eax, 5
    cmp eax, 3      ; 5 > 3 unsigned
    ja .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jb_taken():
    """
    ; Below (unsigned): CF=1
    mov eax, 3
    cmp eax, 5      ; 3 < 5 unsigned, CF=1
    jb .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jae_taken():
    """
    ; Above or Equal (unsigned): CF=0
    mov eax, 5
    cmp eax, 3      ; 5 >= 3 unsigned, CF=0
    jae .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jbe_taken():
    """
    ; Below or Equal (unsigned): CF=1 or ZF=1
    mov eax, 3
    cmp eax, 3      ; 3 <= 3 unsigned, ZF=1
    jbe .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_js_taken():
    """
    ; Sign: SF=1
    mov eax, -1
    test eax, eax   ; SF=1
    js .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jns_taken():
    """
    ; Not Sign: SF=0
    mov eax, 1
    test eax, eax   ; SF=0
    jns .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jo_taken():
    """
    ; Overflow: OF=1
    mov al, 127
    add al, 1       ; 127 + 1 = -128 (overflow), OF=1
    jo .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jno_taken():
    """
    ; No Overflow: OF=0
    mov eax, 1
    add eax, 1      ; No overflow, OF=0
    jno .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jp_taken():
    """
    ; Parity Even: PF=1
    mov al, 0x03    ; 0b00000011, 2 bits set, even parity, PF=1
    test al, al
    jp .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

@pytest.mark.jump
def case_jcc_jnp_taken():
    """
    ; Parity Odd: PF=0
    mov al, 0x01    ; 0b00000001, 1 bit set, odd parity, PF=0
    test al, al
    jnp .taken
    mov ebx, 0
    jmp .done
.taken:
    mov ebx, 1
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1
        }
    }

# =============================================================================
# CALL and RET Instructions
# =============================================================================

@pytest.mark.jump
def case_call_ret_near():
    """
    mov eax, 0
    call .subroutine
    mov ebx, 2      ; After return
    hlt
.subroutine:
    mov eax, 1
    ret
    """
    return {
        'expected_regs': {
            'EAX': 1,
            'EBX': 2
        }
    }

@pytest.mark.jump
def case_call_nested():
    """
    mov eax, 0
    call .func1
    hlt
    
.func1:
    mov eax, 1
    call .func2
    mov eax, 3      ; After func2 returns
    ret
    
.func2:
    mov eax, 2
    ret
    """
    return {
        'expected_regs': {
            'EAX': 3  # Modified after func2 returns
        }
    }

@pytest.mark.jump
def case_call_indirect_reg():
    """
    mov edx, .subroutine
    call edx
    mov ebx, 2
    hlt
.subroutine:
    mov eax, 1
    ret
    """
    return {
        'expected_regs': {
            'EAX': 1,
            'EBX': 2
        }
    }

@pytest.mark.jump
def case_ret_with_stack():
    """
    ; Test that CALL pushes return address correctly
    mov eax, 0
    mov ebx, 0
    call .subroutine
    
    ; Check that we returned correctly
    mov ebx, 42
    hlt
    
.subroutine:
    ; Peek at return address on stack
    mov eax, [esp]  ; Should be address of "mov ebx, 42"
    ret
    """
    return {
        'expected_regs': {
            'EBX': 42
        }
        # EAX will contain return address, don't check exact value
    }

# =============================================================================
# Edge Cases and Complex Scenarios
# =============================================================================

@pytest.mark.jump  
def case_jmp_loop_simple():
    """
    ; Simple loop: increment ECX 5 times
    mov ecx, 0
.loop:
    inc ecx
    cmp ecx, 5
    jl .loop        ; Loop while ECX < 5
    hlt
    """
    return {
        'expected_regs': {
            'ECX': 5
        }
    }

@pytest.mark.jump
def case_jcc_short_and_near():
    """
    ; Test both short (rel8) and near (rel32) conditional jumps
    mov eax, 1
    cmp eax, 1
    je short .near_target   ; Short jump
    mov ebx, 99
    jmp .done
.near_target:
    mov ebx, 1
    cmp ebx, 1
    je .final               ; Near jump (will be 0F 84)
    mov ecx, 99
    jmp .done
.final:
    mov ecx, 2
.done:
    hlt
    """
    return {
        'expected_regs': {
            'EBX': 1,
            'ECX': 2
        }
    }

# =============================================================================
# Test Runners
# =============================================================================

def test_jmp_short_forward():
    run_jmp_test(case_jmp_short_forward)

def test_jmp_short_backward():
    run_jmp_test(case_jmp_short_backward)

def test_jmp_near_forward():
    run_jmp_test(case_jmp_near_forward)

def test_jmp_indirect_reg():
    run_jmp_test(case_jmp_indirect_reg)

def test_jcc_je_taken():
    run_jmp_test(case_jcc_je_taken)

def test_jcc_je_not_taken():
    run_jmp_test(case_jcc_je_not_taken)

def test_jcc_jne_taken():
    run_jmp_test(case_jcc_jne_taken)

def test_jcc_jg_taken():
    run_jmp_test(case_jcc_jg_taken)

def test_jcc_jl_taken():
    run_jmp_test(case_jcc_jl_taken)

def test_jcc_jge_taken():
    run_jmp_test(case_jcc_jge_taken)

def test_jcc_jle_taken():
    run_jmp_test(case_jcc_jle_taken)

def test_jcc_ja_taken():
    run_jmp_test(case_jcc_ja_taken)

def test_jcc_jb_taken():
    run_jmp_test(case_jcc_jb_taken)

def test_jcc_jae_taken():
    run_jmp_test(case_jcc_jae_taken)

def test_jcc_jbe_taken():
    run_jmp_test(case_jcc_jbe_taken)

def test_jcc_js_taken():
    run_jmp_test(case_jcc_js_taken)

def test_jcc_jns_taken():
    run_jmp_test(case_jcc_jns_taken)

def test_jcc_jo_taken():
    run_jmp_test(case_jcc_jo_taken)

def test_jcc_jno_taken():
    run_jmp_test(case_jcc_jno_taken)

def test_jcc_jp_taken():
    run_jmp_test(case_jcc_jp_taken)

def test_jcc_jnp_taken():
    run_jmp_test(case_jcc_jnp_taken)

def test_call_ret_near():
    run_jmp_test(case_call_ret_near)

def test_call_nested():
    run_jmp_test(case_call_nested)

def test_call_indirect_reg():
    run_jmp_test(case_call_indirect_reg)

def test_ret_with_stack():
    run_jmp_test(case_ret_with_stack)

def test_jmp_loop_simple():
    run_jmp_test(case_jmp_loop_simple)

def test_jcc_short_and_near():
    run_jmp_test(case_jcc_short_and_near)
