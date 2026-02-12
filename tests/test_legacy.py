
import pytest
from tests.runner import X86EmuBackend

@pytest.mark.unit
class TestLegacyOps:
    def setup_method(self):
        self.emu = X86EmuBackend()
        self.emu.mem_map(0x1000, 0x1000, 7) # Code
        self.emu.mem_map(0x2000, 0x1000, 3) # Data

    def run_code(self, code):
        self.emu.mem_write(0x1000, code)
        self.emu.start(0x1000, 0x1000 + len(code))

    def test_bcd_daa(self):
        # simple BCD addition: 9 + 1 = 10 (0x10 BCD)
        # MOV AL, 9; ADD AL, 1; DAA
        code = b"\xB0\x09\x04\x01\x27\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") & 0xFF == 0x10
        # Check flags (AF=1, CF=0) - DAA sets AF if lower nibble adjusted
        # 9+1=0xA. 0xA > 9, so AF set, AL+=6 -> 0x10.
        eflags = self.emu.reg_read("EFLAGS")
        assert (eflags & 0x10) != 0 # AF set

    def test_bcd_aaa(self):
        # AAA: AL=0x9, ADD AL, 1 -> AL=0xA. AAA -> AX=0x0100 (AH=1, AL=0)
        # MOV EAX, 9; ADD AL, 1; AAA
        code = b"\xB8\x09\x00\x00\x00\x04\x01\x37\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") & 0xFFFF == 0x0100

    def test_bcd_das(self):
        # DAS: AL=0, SUB AL, 1 -> AL=0xFF. DAS -> AL=0x99, CF=1
        # MOV AL, 0; SUB AL, 1; DAS
        code = b"\xB0\x00\x2C\x01\x2F\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") & 0xFF == 0x99
        assert (self.emu.reg_read("EFLAGS") & 1) != 0 # CF set

    def test_bcd_aas(self):
        # AAS: AX=0x0100, SUB AL, 1 -> AL=0xFF. AAS -> AX=0x0009
        # MOV EAX, 0x100; SUB AL, 1; AAS
        code = b"\xB8\x00\x01\x00\x00\x2C\x01\x3F\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") & 0xFFFF == 0x0009

    def test_bcd_aam(self):
        # AAM: AL=0x5E (94). AAM -> AH=9, AL=4
        # MOV AL, 0x5E; AAM 10
        code = b"\xB0\x5E\xD4\x0A\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") & 0xFFFF == 0x0904

    def test_bcd_aad(self):
        # AAD: AH=9, AL=4. AAD -> AL=94 (0x5E), AH=0
        # MOV EAX, 0x0904; AAD 10
        code = b"\xB8\x04\x09\x00\x00\xD5\x0A\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") & 0xFFFF == 0x005E

    def test_xlat(self):
        # XLAT: EBX points to table, AL is index
        # Table at 0x2000: [0x10, 0x11, 0x12, 0x13]
        # AL=2 -> Res=0x12
        self.emu.mem_write(0x2000, b"\x10\x11\x12\x13")
        # MOV EBX, 0x2000; MOV AL, 2; XLAT
        code = b"\xBB\x00\x20\x00\x00\xB0\x02\xD7\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") & 0xFF == 0x12

    def test_bound_success(self):
        # BOUND r32, m32&32
        # Bounds at 0x2000: Low=0, High=10
        self.emu.mem_write(0x2000, b"\x00\x00\x00\x00\x0A\x00\x00\x00")
        # MOV EAX, 5; BOUND EAX, [0x2000]
        code = b"\xB8\x05\x00\x00\x00\x62\x05\x00\x20\x00\x00\xF4"
        self.run_code(code)
        assert self.emu.reg_read("EAX") == 5

    def test_bound_fault(self):
        # Check #BR (Vector 5)
        # Bounds at 0x2000: Low=0, High=10
        self.emu.mem_write(0x2000, b"\x00\x00\x00\x00\x0A\x00\x00\x00")
        
        # MOV EAX, 20; BOUND EAX, [0x2000]; LEN=6, NOP...
        # 0x1000: B8 14 00 00 00 (MOV EAX, 20)
        # 0x1005: 62 05 00 20 00 00 (BOUND EAX, [0x2000]) -> This should fault
        # 0x100B: F4
        code = b"\xB8\x14\x00\x00\x00\x62\x05\x00\x20\x00\x00\xF4"
        self.emu.mem_write(0x1000, code)
        self.emu.start(0x1000, 0x1000 + len(code))
        
        # Should detect fault
        assert self.emu.get_status() == 2 # Fault
        fault = self.emu.get_fault_info()
        assert fault is not None
        assert fault[0] == 5 # #BR
        
        # Verify EIP points to BOUND instruction (0x1005) because it's a Fault, not Trap
        assert self.emu.reg_read("EIP") == 0x1005
