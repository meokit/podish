import pytest
from tests.runner import X86EmuBackend

# Utility to track handled interrupts
def make_handler(record_list):
    def check(vector):
        record_list.append(vector)
        return 1 # Handled
    return check

def test_div_zero_handled():
    emu = X86EmuBackend()
    handled = []
    emu.set_intr_hook(0, make_handler(handled))
    
    # EAX=100, ECX=0; DIV ECX (F7 F1)
    code = b'\xB8\x64\x00\x00\x00' + b'\x31\xC9' + b'\xF7\xF1'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    # Should not be in Fault state
    assert emu.get_fault_info() is None
    assert 0 in handled

def test_div_zero_fault():
    emu = X86EmuBackend()
    # No hook
    
    # EAX=100, ECX=0; DIV ECX (F7 F1)
    code = b'\xB8\x64\x00\x00\x00' + b'\x31\xC9' + b'\xF7\xF1'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    # Should Fault
    fault = emu.get_fault_info()
    assert fault is not None
    assert fault[0] == 0

def test_into_overflow():
    emu = X86EmuBackend()
    handled = []
    emu.set_intr_hook(4, make_handler(handled))
    
    # AL=0x7F, ADD AL, 1 -> OF=1; INTO (CE)
    code = b'\xB0\x7F' + b'\x04\x01' + b'\xCE'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    assert 4 in handled
    assert emu.get_fault_info() is None

def test_into_no_overflow():
    emu = X86EmuBackend()
    handled = []
    emu.set_intr_hook(4, make_handler(handled))
    
    # Clear OF=0; INTO
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
    assert emu.get_fault_info() is None

def test_unregistered_fault():
    emu = X86EmuBackend()
    # INT 0x42 (CD 42) -> No handler
    code = b'\xCD\x42'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    fault = emu.get_fault_info()
    assert fault is not None
    assert fault[0] == 0x42

def test_idiv_zero_fault():
    emu = X86EmuBackend()
    
    # EAX=100, ECX=0; IDIV ECX (F7 F9)
    code = b'\xB8\x64\x00\x00\x00' + b'\x31\xC9' + b'\xF7\xF9'
    
    emu.mem_map(0x1000, 0x1000, 7)
    emu.mem_write(0x1000, code)
    emu.start(0x1000, 0x1000 + len(code))
    
    fault = emu.get_fault_info()
    assert fault is not None
    assert fault[0] == 0