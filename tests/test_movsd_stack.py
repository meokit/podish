import pytest
from tests.runner import Runner
import struct

def run_test(func):
    runner = Runner()
    asm = func.__doc__
    if not asm:
        raise ValueError(f"No docstring found for {func.__name__}")
    expectations = func()
    runner.run_test(
        name=func.__name__,
        asm=asm,
        check_unicorn=False,  # Disable Unicorn for stack tests
        **expectations
    )

@pytest.mark.sse
def case_movsd_stack_write():
    """
    ; Test MOVSD writing double to stack
    mov esp, 0x8000
    
    ; Load 1.722 into XMM0 using immediate values
    ; 1.722 = 0x3FFBB851EB851EB8
    mov dword [0x2000], 0xEB851EB8
    mov dword [0x2004], 0x3FFBB851
    movsd xmm0, [0x2000]
    
    ; Write to stack using MOVSD
    sub esp, 16
    movsd [esp], xmm0
    
    ; Read back to verify
    mov eax, [esp]
    mov edx, [esp+4]
    
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0xEB851EB8,
            'EDX': 0x3FFBB851
        }
    }

@pytest.mark.sse
def case_movsd_stack_read():
    """
    ; Test MOVSD reading double from stack
    mov esp, 0x8000
    
    ; Push 1.722 onto stack (0x3FFBB851EB851EB8)
    push 0x3FFBB851  ; High 32 bits
    push 0xEB851EB8  ; Low 32 bits
    
    ; Read with MOVSD
    movsd xmm0, [esp]
    
    ; Write back to memory for verification
    movsd [0x2000], xmm0
    
    ; Also load into registers
    mov eax, [0x2000]
    mov edx, [0x2004]
    
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0xEB851EB8,
            'EDX': 0x3FFBB851
        },
        'expected_write': {
            0x2000: 0xEB851EB8,
            0x2004: 0x3FFBB851
        }
    }

@pytest.mark.sse
def case_movsd_param_passing():
    """
    ; Simulate function call with double parameter
    ; Setup: push double 1.722 as parameter
    mov esp, 0x8000
    push 0x3FFBB851  ; High 32 bits
    push 0xEB851EB8  ; Low 32 bits
    push 0x0         ; Simulate return address
    
    ; Simulate function prologue
    push ebp
    mov ebp, esp
    
    ; Read parameter from 0x8(%ebp) like fcvtbuf does
    movsd xmm0, [ebp+8]
    
    ; Store result
    movsd [0x2000], xmm0
    
    ; Verify
    mov eax, [0x2000]
    mov edx, [0x2004]
    
    ; Cleanup
    pop ebp
    
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0xEB851EB8,
            'EDX': 0x3FFBB851
        }
    }

def test_movsd_stack_write(): run_test(case_movsd_stack_write)
def test_movsd_stack_read(): run_test(case_movsd_stack_read)
def test_movsd_param_passing(): run_test(case_movsd_param_passing)
