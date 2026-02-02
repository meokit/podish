import pytest
from tests.runner import X86EmuBackend

def test_tlb_dirty_bit_sync():
    emu = X86EmuBackend()
    
    # 1. Map a page and write to it
    addr = 0x50000
    emu.mem_map(addr, 0x1000, 3) # RW Clean
    
    # Initial state should be NOT dirty
    assert not emu.mem_is_dirty(addr)
    
    emu.mem_write(addr, b'\x42')
    # After write, should be dirty
    assert emu.mem_is_dirty(addr)
    
    assert emu.mem_read(addr, 1) == b'\x42'

def test_tlb_eviction_dirty_sync():
    emu = X86EmuBackend()
    
    # Map many pages to force eviction
    base = 0x100000
    # Dirty some pages
    for i in range(10):
        addr = base + i*0x1000
        emu.mem_map(addr, 0x1000, 3)
        emu.mem_write(addr, b'\xCC')
        assert emu.mem_is_dirty(addr)

    # Force eviction by accessing many other pages
    for i in range(10, 400):
        addr = base + i*0x1000
        emu.mem_map(addr, 0x1000, 3)
        emu.mem_read(addr, 1) # Only read, not dirtying these

    # Check the original dirty pages. 
    # Even after eviction, the dirty bit should have been synced back to the page table.
    for i in range(10):
        addr = base + i*0x1000
        assert emu.mem_is_dirty(addr), f"Page 0x{addr:X} should still be marked dirty after eviction"
        assert emu.mem_read(addr, 1) == b'\xCC'
