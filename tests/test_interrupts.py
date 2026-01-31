
import pytest
from tests.runner import X86EmuBackend

# Utility to track handled interrupts
def make_handler(record_list):
    def check(vector):
        record_list.append(vector)
        return 1 # Handled
    return check

def make_unhandled_handler(record_list):
    def check(vector):
        record_list.append(vector)
        return 0 # Not Handled
    return check

def test_div_zero_handled():
    emu = X86EmuBackend()
    
    # Register hook for Vector 0
    handled = []
    emu.set_intr_hook(0, make_handler(handled))
    
    # EAX=100, ECX=0
    # DIV ECX (F7 F1)
    code = b'\xB8\x64\x00\x00\x00' + b'\x31\xC9' + b'\xF7\xF1'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    # Should be Handled (Running or Stopped gracefully, not Fault)
    # Actually, after interrupt, we usually continue? 
    # Our simple loop might continue.
    # But since we didn't fault, status should be Running/Stopped.
    assert emu.state.status != 2
    assert 0 in handled

def test_div_zero_fault():
    emu = X86EmuBackend()
    # No hook
    
    # EAX=100, ECX=0
    # DIV ECX (F7 F1)
    code = b'\xB8\x64\x00\x00\x00' + b'\x31\xC9' + b'\xF7\xF1'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    # Should Fault
    assert emu.state.status == 2
    assert emu.fault_vector == 0

def test_into_overflow():
    emu = X86EmuBackend()
    handled = []
    emu.set_intr_hook(4, make_handler(handled))
    
    # Set OF=1
    # INTO (CE)
    # To set OF: 0x7F + 1 (signed overflow for byte)
    # AL=0x7F, ADD AL, 1
    code = b'\xB0\x7F' + b'\x04\x01' + b'\xCE'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    assert 4 in handled
    assert emu.state.status != 2

def test_into_no_overflow():
    emu = X86EmuBackend()
    handled = []
    emu.set_intr_hook(4, make_handler(handled))
    
    # Clear OF=0
    # INTO
    code = b'\xB0\x00' + b'\x04\x00' + b'\xCE'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    assert 4 not in handled

def test_syscall_int80():
    emu = X86EmuBackend()
    handled = []
    emu.set_intr_hook(0x80, make_handler(handled))
    
    # INT 0x80 (CD 80)
    code = b'\xCD\x80'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    assert 0x80 in handled
    assert emu.state.status != 2

def test_unregistered_fault():
    emu = X86EmuBackend()
    # INT 0x42 (CD 42) -> No handler
    code = b'\xCD\x42'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    assert emu.state.status == 2
    assert emu.fault_vector == 0x42

def test_idiv_zero_fault():
    emu = X86EmuBackend()
    
    # EAX=100, ECX=0
    # IDIV ECX (F7 F9)
    code = b'\xB8\x64\x00\x00\x00' + b'\x31\xC9' + b'\xF7\xF9'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    assert emu.state.status == 2
    assert emu.fault_vector == 0
