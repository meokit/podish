"""
Exception Precision Tests

Validates that EIP is precisely correct when exceptions occur:
- Division by zero (#DE, vector 0)
- Invalid opcode (#UD, vector 6)
- Page fault (#PF, vector 14)
- Software interrupts (INT n)

Also validates that:
- Unhandled exceptions stop the emulator cleanly (no runaway)
- Interrupt handlers that modify EIP cause correct resumption
"""

import pytest
from tests.runner import X86EmuBackend, Runner

def compile_asm(asm):
    runner = Runner()
    return runner.compile(asm)


# =============================================================================
# Division by Zero - EIP Precision
# =============================================================================

class TestDivZeroPrecision:
    """DIV/IDIV by zero: EIP must point to the faulting instruction start."""

    def test_div32_zero_eip(self):
        """DIV ECX (32-bit) with ECX=0: EIP -> DIV instruction"""
        emu = X86EmuBackend()
        
        # MOV EAX, 100  (B8 64 00 00 00) - 5 bytes @ 0x1000
        # XOR ECX, ECX  (31 C9)          - 2 bytes @ 0x1005
        # DIV ECX       (F7 F1)          - 2 bytes @ 0x1007
        # NOP           (90)             - 1 byte  @ 0x1009
        asm = """
        mov eax, 100
        xor ecx, ecx
        div ecx
        nop
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2, "Should fault"
        fault = emu.get_fault_info()
        assert fault is not None
        assert fault[0] == 0, "Should be #DE (vector 0)"
        
        eip = emu.reg_read('EIP')
        assert eip == 0x1007, f"EIP should point to DIV instruction (0x1007), got 0x{eip:X}"

    def test_idiv32_zero_eip(self):
        """IDIV ECX (32-bit) with ECX=0: EIP -> IDIV instruction"""
        emu = X86EmuBackend()
        
        # MOV EAX, 100  (B8 64 00 00 00) - 5 bytes @ 0x1000
        # XOR ECX, ECX  (31 C9)          - 2 bytes @ 0x1005
        # IDIV ECX      (F7 F9)          - 2 bytes @ 0x1007
        asm = """
        mov eax, 100
        xor ecx, ecx
        idiv ecx
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2
        fault = emu.get_fault_info()
        assert fault[0] == 0
        
        eip = emu.reg_read('EIP')
        assert eip == 0x1007, f"EIP should point to IDIV (0x1007), got 0x{eip:X}"

    def test_div8_zero_eip(self):
        """DIV CL (8-bit) with CL=0: EIP -> DIV instruction"""
        emu = X86EmuBackend()
        
        # MOV AX, 100   (66 B8 64 00)    - 4 bytes @ 0x1000
        # XOR ECX, ECX  (31 C9)          - 2 bytes @ 0x1004
        # DIV CL        (F6 F1)          - 2 bytes @ 0x1006
        asm = """
        mov ax, 100
        xor ecx, ecx
        div cl
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2
        fault = emu.get_fault_info()
        assert fault[0] == 0
        
        eip = emu.reg_read('EIP')
        assert eip == 0x1006, f"EIP should point to DIV CL (0x1006), got 0x{eip:X}"

    def test_div32_overflow_eip(self):
        """DIV overflow (quotient > 0xFFFFFFFF): EIP -> DIV instruction"""
        emu = X86EmuBackend()
        
        # MOV EDX, 1    (BA 01 00 00 00) - 5 bytes @ 0x1000
        # MOV EAX, 0    (B8 00 00 00 00) - 5 bytes @ 0x1005
        # MOV ECX, 1    (B9 01 00 00 00) - 5 bytes @ 0x100A
        # DIV ECX       (F7 F1)          - 2 bytes @ 0x100F
        # EDX:EAX = 0x100000000 / 1 = 0x100000000 -> overflow
        asm = """
        mov edx, 1
        mov eax, 0
        mov ecx, 1
        div ecx
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2
        fault = emu.get_fault_info()
        assert fault[0] == 0, "Should be #DE for overflow"
        
        eip = emu.reg_read('EIP')
        assert eip == 0x100F, f"EIP should point to DIV (0x100F), got 0x{eip:X}"


# =============================================================================
# Division by Zero - Handler behavior
# =============================================================================

class TestDivZeroHandler:
    """When DIV0 handler is registered, it should be called and can redirect EIP."""

    def test_div_zero_handled_continues(self):
        """Handler marks as handled -> emulator should NOT fault."""
        emu = X86EmuBackend()
        handled = []
        
        def handler(vector):
            handled.append(vector)
            return 1  # Handled
        
        emu.set_intr_hook(0, handler)
        
        asm = """
        mov eax, 100
        xor ecx, ecx
        div ecx
        nop
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert 0 in handled, "Handler should be called"
        assert emu.get_fault_info() is None, "Should not be in fault state"

    def test_div_zero_handler_redirects_eip(self):
        """Handler sets EIP to skip over the faulting instruction."""
        emu = X86EmuBackend()
        redirected = []
        
        def handler(vector):
            redirected.append(vector)
            # Skip past the DIV instruction to HLT
            emu.reg_write('EIP', 0x100C)
            return 1  # Handled
        
        emu.set_intr_hook(0, handler)
        
        # MOV EAX, 100  (5 bytes) @ 0x1000
        # XOR ECX, ECX  (2 bytes) @ 0x1005
        # DIV ECX       (2 bytes) @ 0x1007
        # NOP           (1 byte)  @ 0x1009
        # NOP           (1 byte)  @ 0x100A
        # HLT           (1 byte)  @ 0x100B
        # NOP           (1 byte)  @ 0x100C <-- set to here
        # NOP           (1 byte)  @ 0x100D
        # HLT           (1 byte)  @ 0x100E
        asm = """
        mov eax, 100
        xor ecx, ecx
        div ecx
        nop
        nop
        hlt
        nop
        nop
        hlt
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert 0 in redirected
        # After redirect, emulator should have reached HLT and stopped
        eip = emu.reg_read('EIP')
        assert eip == 0x100E, f"Should have stop at HLT, got 0x{eip:X}"


# =============================================================================
# Software Interrupt - EIP Precision
# =============================================================================

class TestSoftwareInterruptPrecision:
    """INT n: EIP should be PAST the INT instruction when handler is called."""
    
    def test_int80_eip_after_instruction(self):
        """INT 0x80: EIP should point past INT instruction."""
        emu = X86EmuBackend()
        captured_eip = []
        
        def handler(vector):
            captured_eip.append(emu.reg_read('EIP'))
            return 1  # Handled
        
        emu.set_intr_hook(0x80, handler)
        
        # NOP           (90)    - 1 byte  @ 0x1000
        # INT 0x80      (CD 80) - 2 bytes @ 0x1001
        # HLT           (F4)    - 1 byte  @ 0x1003
        asm = """
        nop
        int 0x80
        hlt
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert len(captured_eip) == 1
        assert captured_eip[0] == 0x1003, \
            f"EIP during INT handler should be 0x1003 (past INT), got 0x{captured_eip[0]:X}"

    def test_int_unhandled_faults_cleanly(self):
        """INT with no handler should fault, not crash."""
        emu = X86EmuBackend()
        
        # INT 0x42 (CD 42) - 2 bytes @ 0x1000
        code = b'\xCD\x42'
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2, "Should be in fault state"
        fault = emu.get_fault_info()
        assert fault is not None
        assert fault[0] == 0x42

    def test_int_handler_redirects_eip(self):
        """INT handler sets EIP to different location -> execution continues there."""
        emu = X86EmuBackend()
        
        def handler(vector):
            emu.reg_write('EIP', 0x1010)  # Jump to NOP sled
            return 1
        
        emu.set_intr_hook(0x80, handler)
        
        # INT 0x80 (CD 80) @ 0x1000 - 2 bytes
        # UD2      (0F 0B) @ 0x1002 - should NOT be reached
        # ... gap ...
        # NOP (90) @ 0x1010
        # HLT (F4) @ 0x1011         - Stop AT HLT
        
        code_main = b'\xCD\x80' + b'\x0F\x0B'  # INT 0x80 + UD2
        code_target = b'\x90\xF4'               # NOP + HLT
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code_main)
        emu.mem_write(0x1010, code_target)
        emu.start(0x1000, 0x1012)
        
        # Should have jumped to 0x1010, executed NOP
        eip = emu.reg_read('EIP')
        assert eip == 0x1011, f"Should have executed NOP at 0x1010, EIP=0x{eip:X}"
        assert emu.get_fault_info() is None, "Should NOT have faulted (UD2 was skipped)"


# =============================================================================
# Invalid Opcode (#UD) - EIP Precision
# =============================================================================

class TestInvalidOpcodePrecision:
    """UD2: EIP must point to the UD2 instruction itself."""
    
    def test_ud2_eip(self):
        """UD2 (0F 0B): EIP should point to UD2 start."""
        emu = X86EmuBackend()
        
        # NOP  (90)    - 1 byte  @ 0x1000
        # NOP  (90)    - 1 byte  @ 0x1001
        # UD2  (0F 0B) - 2 bytes @ 0x1002
        code = b'\x90\x90\x0F\x0B'
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2
        fault = emu.get_fault_info()
        assert fault is not None
        assert fault[0] == 6, "Should be #UD (vector 6)"
        
        eip = emu.reg_read('EIP')
        assert eip == 0x1002, f"EIP should point to UD2 (0x1002), got 0x{eip:X}"


# =============================================================================
# Page Fault (#PF) - EIP Precision
# =============================================================================

class TestPageFaultPrecision:
    """Page faults: EIP must point to the faulting instruction."""
    
    def test_pf_read_eip(self):
        """MOV EAX, [unmapped]: EIP -> MOV instruction."""
        emu = X86EmuBackend()
        
        # NOP               (90)             - 1 byte  @ 0x1000
        # MOV EAX, [0x30000] (A1 00 00 03 00) - 5 bytes @ 0x1001
        asm = """
        nop
        mov eax, [0x30000]
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2
        fault = emu.get_fault_info()
        assert fault[0] == 14, "Should be #PF (vector 14)"
        
        eip = emu.reg_read('EIP')
        assert eip == 0x1001, f"EIP should point to MOV (0x1001), got 0x{eip:X}"

    def test_pf_write_eip(self):
        """MOV [unmapped], EAX: EIP -> MOV instruction."""
        emu = X86EmuBackend()
        
        # NOP                  (90)              - 1 byte  @ 0x1000
        # MOV [0x40000], EAX   (A3 00 00 04 00)  - 5 bytes @ 0x1001
        asm = """
        nop
        mov [0x40000], eax
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2
        eip = emu.reg_read('EIP')
        assert eip == 0x1001, f"EIP should point to MOV (0x1001), got 0x{eip:X}"

    def test_pf_handler_maps_and_retries(self):
        """Page fault handler maps the page and returns True -> instruction retries successfully."""
        emu = X86EmuBackend()
        faults = []
        
        def fault_cb(addr, is_write):
            faults.append((addr, is_write))
            # Map the faulting page
            emu.mem_map(addr & 0xFFFFF000, 0x1000, 3)
            emu.mem_write(addr, b'\xAA\xBB\xCC\xDD')
            return True  # Handled: retry the instruction
        
        emu.set_fault_callback(fault_cb)
        
        # MOV EAX, [0x50000] (A1 00 00 05 00) - 5 bytes @ 0x1000
        # HLT                (F4)             - 1 byte  @ 0x1005
        asm = """
        mov eax, [0x50000]
        hlt
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert len(faults) == 1, f"Should fault once, got {len(faults)}"
        assert faults[0][0] == 0x50000
        assert emu.get_status() != 2, "Should not be in fault state after handler mapped page"
        
        eax = emu.reg_read('EAX')
        assert eax == 0xDDCCBBAA, f"EAX should contain data from mapped page, got 0x{eax:X}"

    def test_pf_handler_unhandled_stops(self):
        """Page fault handler returns False -> emulator stops with fault."""
        emu = X86EmuBackend()
        faults = []
        
        def fault_cb(addr, is_write):
            faults.append((addr, is_write))
            return False  # Not handled
        
        emu.set_fault_callback(fault_cb)
        
        asm = "mov eax, [0x60000]"
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert len(faults) == 1, "Should only be called once (no infinite loop)"
        assert emu.get_status() == 2, "Should fault"
        eip = emu.reg_read('EIP')
        assert eip == 0x1000, f"EIP should point to MOV (0x1000), got 0x{eip:X}"


# =============================================================================
# Mixed Scenarios - No Runaway
# =============================================================================

class TestNoRunaway:
    """Emulator must never run away: unhandled exceptions always stop cleanly."""

    def test_multiple_faults_no_crash(self):
        """Sequential fault-inducing instructions: each faults independently."""
        emu = X86EmuBackend()
        
        # First: DIV by zero
        asm = """
        mov eax, 1
        xor ecx, ecx
        div ecx
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        assert emu.get_status() == 2
        eip1 = emu.reg_read('EIP')
        
        # Resume from same EIP without fixing -> should fault again
        emu.start(eip1, 0x1000 + len(code))
        assert emu.get_status() == 2, "Should fault again, not run away"
        eip2 = emu.reg_read('EIP')
        assert eip1 == eip2, "EIP should be the same (stuck on same instruction)"

    def test_fault_then_continue(self):
        """Fault, then manually advance EIP past bad instruction, then continue."""
        emu = X86EmuBackend()
        
        # MOV EAX, 42   (B8 2A 00 00 00) - 5 bytes @ 0x1000
        # XOR ECX, ECX  (31 C9)          - 2 bytes @ 0x1005
        # DIV ECX       (F7 F1)          - 2 bytes @ 0x1007
        # MOV EBX, 99   (BB 63 00 00 00) - 5 bytes @ 0x1009
        # HLT           (F4)             - 1 byte  @ 0x100E
        asm = """
        mov eax, 42
        xor ecx, ecx
        div ecx
        mov ebx, 99
        hlt
        """
        code = compile_asm(asm)
        
        emu.mem_map(0x1000, 0x1000, 7)
        emu.mem_write(0x1000, code)
        emu.start(0x1000, 0x1000 + len(code))
        
        # Should fault at DIV
        assert emu.get_status() == 2
        assert emu.reg_read('EIP') == 0x1007
        
        # Skip past DIV (2 bytes) and continue
        emu.reg_write('EIP', 0x1009)
        emu.start(0x1009, 0x1000 + len(code))
        
        # Should have executed MOV EBX, 99 + HLT
        assert emu.reg_read('EBX') == 99, "EBX should be 99 after resuming"
