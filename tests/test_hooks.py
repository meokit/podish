import pytest
import sys
import os

# Add current dir to path to find runner
sys.path.append(os.path.dirname(__file__))

from runner import X86Emu

@pytest.fixture
def x86():
    return X86Emu()

def test_invalid_opcode(x86):
    # 0x0F 0x0B is UD2
    code = b'\x0F\x0B' 
    x86.mem_map(0x1000, 0x1000, 7)
    x86.mem_write(0x1000, code)
    
    # Hook for Vector 6 (#UD)
    hook_called = False
    def intr_hook(vector):
        nonlocal hook_called
        hook_called = True
        return 0 # Fault
        
    x86.set_intr_hook(6, intr_hook)
    x86.reg_write('EIP', 0x1000)
    
    # Run
    status = x86.step()
    
    assert hook_called
    assert status == 2 # Fault

def test_int80(x86):
    # CD 80
    code = b'\xCD\x80'
    x86.mem_map(0x1000, 0x1000, 7)
    x86.mem_write(0x1000, code)
    
    hook_vector = -1
    def intr_hook(vector):
        nonlocal hook_vector
        hook_vector = vector
        return 1 # Handled
    
    x86.set_intr_hook(0x80, intr_hook)
    x86.reg_write('EIP', 0x1000)
    
    x86.step()
    
    assert hook_vector == 0x80

def test_decode_fault(x86):
    # F0 F0 F0 ... too many prefixes -> Decode Fault -> #UD (6)
    code = b'\xF0' * 16
    x86.mem_map(0x1000, 0x1000, 7)
    x86.mem_write(0x1000, code)
    
    hook_called = False
    def intr_hook(vector):
        nonlocal hook_called
        if vector == 6:
            hook_called = True
        return 1 # Handle it
        
    x86.set_intr_hook(6, intr_hook)
    x86.reg_write('EIP', 0x1000)
    
    x86.step()
    assert hook_called