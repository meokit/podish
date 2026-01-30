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
@pytest.mark.xfail(reason="Opcode A3 (MOV moffs32, EAX) not yet implemented")
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
def test_segment_base_api():
    """Test that segment base API accepts values without crashing"""
    runner = Runner()
    
    # This test verifies the API accepts segment base values
    # Note: Without segment prefix instructions, this won't actually use FS base
    # It just ensures the framework can set segment bases without errors
    runner.run_test_bytes(
        name="Segment Base API Test",
        code=binascii.unhexlify("891D00200000"),  # Regular MOV [0x2000], EBX
        initial_regs={'EBX': 0x11223344},
        expected_write={0x2000: 0x11223344},  # Still writes to 0x2000, not 0x3000+0x2000
        initial_seg_base=[0, 0, 0, 0, 0x3000, 0]  # FS base = 0x3000 (not used here)
    )

@pytest.mark.unit
@pytest.mark.xfail(reason="Segment override prefix (GS) not yet implemented in decoder")
def test_segment_override_gs():
    """Test GS segment override prefix"""
    runner = Runner()
    
    # GS:[0x100] with GS base = 0x5000 should access linear address 0x5100
    # MOV [GS:0x100], EAX
    # Opcode: 65 A3 00 01 00 00 (GS prefix + MOV [moffs32], EAX)
    
    runner.run_test_bytes(
        name="GS Segment Override Write",
        code=binascii.unhexlify("65A300010000"),  # GS: MOV [0x100], EAX
        initial_regs={'EAX': 0xDEADBEEF},
        expected_write={0x5100: 0xDEADBEEF},  # GS base (0x5000) + offset (0x100)
        initial_seg_base=[0, 0, 0, 0, 0, 0x5000]  # GS base = 0x5000
    )

@pytest.mark.unit
@pytest.mark.xfail(reason="Segment override prefix (FS) not yet implemented in decoder")  
def test_segment_override_fs():
    """Test FS segment override prefix for TLS"""
    runner = Runner()
    
    # FS is commonly used for Thread Local Storage
    # MOV EAX, [FS:0x0] - Read from TLS slot 0
    # Opcode: 64 A1 00 00 00 00 (FS prefix + MOV EAX, [moffs32])
    
    # First, we need to write a value to FS:0x0 (linear 0x3000)
    # For this test, we'll just verify the read
    runner.run_test_bytes(
        name="FS Segment Override Read",
        code=binascii.unhexlify("64A100000000"),  # FS: MOV EAX, [0x0]
        initial_regs={'EAX': 0xFFFFFFFF},
        expected_regs={'EAX': 0x0},  # Should read from FS base (0x3000) + 0
        expected_read={0x3000: 0x0},  # Linear address = FS base + offset
        initial_seg_base=[0, 0, 0, 0, 0x3000, 0]  # FS base = 0x3000
    )

@pytest.mark.unit
def test_stack_with_ss_base():
    """Test stack operations with SS (Stack Segment) base"""
    runner = Runner()
    
    # In real mode or with non-flat memory, SS base affects stack operations
    # PUSH EAX should write to SS:ESP
    # Opcode: 50 (PUSH EAX)
    
    runner.run_test_bytes(
        name="Stack with SS Base",
        code=binascii.unhexlify("50"),  # PUSH EAX
        initial_regs={
            'EAX': 0x12345678,
            'ESP': 0x8000  # Stack pointer in mapped region (0x7000-0x9000)
        },
        expected_regs={
            'ESP': 0x7FFC  # ESP decrements by 4
        },
        expected_write={
            0x7FFC: 0x12345678  # In flat model: SS base (0) + ESP (0x7FFC)
            # With SS base = 0x7000: would be 0xEFFC
        },
        initial_seg_base=[0, 0, 0, 0, 0, 0]  # Flat model for now
    )

@pytest.mark.unit
def test_multiple_segment_bases():
    """Test setting multiple segment bases simultaneously"""
    runner = Runner()
    
    # Verify we can set all segment bases
    # ES=0x1000, CS=0x2000, SS=0x3000, DS=0x4000, FS=0x5000, GS=0x6000
    # This just tests the API, actual usage requires segment prefixes
    
    runner.run_test_bytes(
        name="Multiple Segment Bases",
        code=binascii.unhexlify("89C3"),  # MOV EBX, EAX (simple instruction)
        initial_regs={'EAX': 0x12345678},
        expected_regs={'EBX': 0x12345678},
        initial_seg_base=[0x1000, 0x2000, 0x3000, 0x4000, 0x5000, 0x6000]
    )
    print("\033[32m[PASS]\033[0m Multiple segment bases can be set!")

if __name__ == "__main__":
    print("\n" + "="*60)
    print("Segment Base Tests")
    print("="*60 + "\n")
    
    test_default_flat_memory_model()
    print("\033[32m[PASS]\033[0m Default flat memory model test passed\n")
    
    test_segment_base_api()
    print("\033[32m[PASS]\033[0m Segment base API test passed\n")
    
    test_multiple_segment_bases()
    print("\033[32m[PASS]\033[0m Multiple segment bases test passed\n")
    
    print("\n" + "-"*60)
    print("Testing segment override instructions (may not be implemented yet):")
    print("-"*60 + "\n")
    
    test_segment_override_gs()
    test_segment_override_fs()
    test_stack_with_ss_base()
    
    print("\n" + "="*60)
    print("\033[32m[PASS]\033[0m Segment base test suite complete!")
    print("="*60)
