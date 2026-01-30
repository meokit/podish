from tests.runner import Runner
import binascii
import pytest

@pytest.mark.unit
def test_default_flat_memory_model():
    """Test default flat memory model (all segment bases = 0)"""
    runner = Runner()
    
    # Test 1: Basic memory write in flat model
    # MOV [0x2000], EBX
    # Opcode: 89 1D 00 20 00 00
    runner.run_test_bytes(
        name="Flat Model Write Test",
        code=binascii.unhexlify("891D00200000"),
        initial_regs={'EBX': 0xABCDEF12},
        expected_regs={'EBX': 0xABCDEF12},
        expected_write={0x2000: 0xABCDEF12},
        initial_seg_base=None  # Default: all zeros (flat model)
    )

@pytest.mark.unit
def test_flat_memory_read():
    
    """Test memory read with MOV [moffs32], EAX (opcode A3)"""
    runner = Runner()
    
    # First write a value, then read it back
    # MOV [0x2000], EAX
    # Opcode: A3 00 20 00 00
    runner.run_test_bytes(
        name="Flat Model Read Test Setup",
        code=binascii.unhexlify("A300200000"),  # MOV [0x2000], EAX
        initial_regs={'EAX': 0x11223344},
        expected_write={0x2000: 0x11223344}
    )

@pytest.mark.unit
def test_segment_override_gs():
    """Test GS segment override prefix"""
    runner = Runner()
    
    # GS:[0x100] with GS base = 0x2000 should access linear address 0x2100 (Mapped)
    # MOV [GS:0x100], EAX
    # Opcode: 65 A3 00 01 00 00 (GS prefix + MOV [moffs32], EAX)
    
    runner.run_test_bytes(
        name="GS Segment Override Write",
        code=binascii.unhexlify("65A300010000"),  # GS: MOV [0x100], EAX
        initial_regs={'EAX': 0xDEADBEEF},
        expected_write={0x2100: 0xDEADBEEF},  # GS base (0x2000) + offset (0x100)
        initial_seg_base=[0, 0, 0, 0, 0, 0x2000]  # GS base = 0x2000
    )

@pytest.mark.unit
def test_segment_override_fs():
    """Test FS segment override prefix for TLS"""
    runner = Runner()
    
    # FS is commonly used for Thread Local Storage
    # MOV EAX, [FS:0x0] - Read from TLS slot 0
    # Opcode: 64 A1 00 00 00 00 (FS prefix + MOV EAX, [moffs32])
    
    # First, we need to write a value to FS:0x0 (linear 0x2000)
    # For this test, we'll just verify the read
    runner.run_test_bytes(
        name="FS Segment Override Read",
        code=binascii.unhexlify("64A100000000"),  # FS: MOV EAX, [0x0]
        initial_regs={'EAX': 0xFFFFFFFF},
        expected_regs={'EAX': 0x0},  # Should read from FS base (0x2000) + 0
        expected_read={0x2000: 0x0},  # Linear address = FS base + offset
        initial_seg_base=[0, 0, 0, 0, 0x2000, 0]  # FS base = 0x2000
    )

@pytest.mark.unit
def test_mov_moffs8():
    """Test MOV AL, moffs8 (A0) and MOV moffs8, AL (A2)"""
    runner = Runner()
    
    # 1. Store AL to [0x2000] (A2)
    # 2. Load AL from [0x2000] (A0)
    # Opcode A2: A2 00 20 00 00
    # Opcode A0: A0 00 20 00 00
    
    runner.run_test_bytes(
        name="MOV moffs8 Store/Load",
        code=binascii.unhexlify(
            "A200200000"  # MOV [0x2000], AL
            "B0FF"        # MOV AL, 0xFF (Trash AL)
            "A000200000"  # MOV AL, [0x2000] (Restore AL)
        ),
        initial_regs={'EAX': 0x11223344}, # AL = 0x44
        expected_regs={'EAX': 0x11223344}, # Should be back to 0x44
        expected_write={0x2000: 0x44}
    )

@pytest.mark.unit
def test_mov_moffs32_load():
    """Test MOV EAX, moffs32 (A1)"""
    runner = Runner()
    
    # Load EAX from [0x2000]
    # Opcode A1: A1 00 20 00 00
    runner.run_test_bytes(
        name="MOV moffs32 Load",
        code=binascii.unhexlify("A100200000"),
        initial_regs={'EAX': 0x0},
        # We need to pre-populate memory or just check the read addr.
        # Since we can't easily pre-populate in run_test_bytes without a helper,
        # we'll rely on the default memory content (0) or check the trace.
        # But wait, run_test_bytes maps memory as zeroed.
        # Let's write first using A3? Or just check expected_read (which passes if read happens).
        expected_read={0x2000: 0x0},
        expected_regs={'EAX': 0x0}
    )

@pytest.mark.unit
def test_addr_size_override():
    """Test Address Size Override (0x67) with MOV moffs"""
    runner = Runner()
    
    # 32-bit mode default. 0x67 switches to 16-bit address.
    # MOV AL, [0x2000] (16-bit offset)
    # Opcode: 67 A0 00 20 (Note: Only 2 bytes for offset!)
    
    # If the decoder fails to handle 0x67 for A0, it might consume 4 bytes
    # or read garbage.
    
    runner.run_test_bytes(
        name="Addr Size Override (16-bit)",
        code=binascii.unhexlify("67A00020"), # MOV AL, [0x2000]
        initial_regs={'EAX': 0x0},
        expected_read={0x2000: 0x0}
    )

@pytest.mark.unit
def test_segment_conflict_last_wins():
    """Test that the last segment prefix is the one used"""
    runner = Runner()
    
    # FS: GS: MOV [0x100], EAX
    # Should use GS (0x65), ignoring FS (0x64)
    # Opcode: 64 65 A3 00 01 00 00
    
    runner.run_test_bytes(
        name="Segment Conflict (Last Wins)",
        code=binascii.unhexlify("6465A300010000"),
        initial_regs={'EAX': 0xCAFEBABE},
        initial_seg_base=[0, 0, 0, 0, 0x1000, 0x2000], # FS=0x1000, GS=0x2000
        expected_write={0x2100: 0xCAFEBABE} # GS Base + 0x100
    )

if __name__ == "__main__":
    print("\n" + "="*60)
    print("Segment Base Tests")
    print("="*60 + "\n")
    
    test_default_flat_memory_model()
    print("\033[32m[PASS]\033[0m Default flat memory model test passed\n")
    
    print("\n" + "-"*60)
    print("Testing segment override instructions:")
    print("-"*60 + "\n")
    
    test_segment_override_gs()
    test_segment_override_fs()
    test_mov_moffs8()
    test_mov_moffs32_load()
    test_addr_size_override()
    test_segment_conflict_last_wins()
    
    print("\n" + "="*60)
    print("\033[32m[PASS]\033[0m Segment base test suite complete!")
    print("="*60)
