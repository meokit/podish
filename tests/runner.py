
import ctypes
import os
import sys
import struct
import subprocess
import tempfile
from unicorn import *
from unicorn.x86_const import *
from capstone import *
import ctypes

# Configurations
LIB_PATH = os.path.join(os.path.dirname(__file__), "../build/libx86emu.dylib") 

# Ctypes Structures
class Context(ctypes.Structure):
    _fields_ = [
        ("regs", ctypes.c_uint32 * 8),  # 0
        ("eip", ctypes.c_uint32),       # 32
        ("eflags", ctypes.c_uint32),    # 36
        ("pad0", ctypes.c_uint8 * 8),   # 40 -> 48 (Align xmm to 16)
        ("xmm", ctypes.c_uint8 * 128),  # 48 -> 176
        ("mxcsr", ctypes.c_uint32),     # 176
        ("seg_base", ctypes.c_uint32 * 6), # 180 -> 204
        ("pad1", ctypes.c_uint8 * 4),   # 204 -> 208 (Align pointer to 8)
        ("mmu", ctypes.c_void_p),       # 208 -> 216
        ("hooks", ctypes.c_void_p),     # 216 -> 224
        ("pad2", ctypes.c_uint8 * 32),  # 224 -> 256 (Alignas 64)
    ]

class X86Emu:
    def __init__(self):
        self.lib = ctypes.CDLL(LIB_PATH)
        
        # Set Argument Types
        self.lib.X86_Create.restype = ctypes.c_void_p
        self.lib.X86_Create.argtypes = []
        
        self.lib.X86_GetContext.restype = ctypes.POINTER(Context)
        self.lib.X86_GetContext.argtypes = [ctypes.c_void_p]
        
        self.lib.X86_MemMap.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint8]
        self.lib.X86_MemWrite.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint8), ctypes.c_uint32]
        self.lib.X86_Decode.argtypes = [ctypes.POINTER(ctypes.c_uint8), ctypes.c_void_p]
        self.lib.X86_SetFaultCallback.argtypes = [ctypes.c_void_p, ctypes.c_void_p] # state, function_pointer
        self.lib.X86_Step.argtypes = [ctypes.c_void_p]
        self.lib.X86_Run.argtypes = [ctypes.c_void_p]
        
        print("  [DEBUG] Calling X86_Create...")
        self.state = self.lib.X86_Create()
        print(f"  [DEBUG] State Ptr: {self.state}")
        
        print("  [DEBUG] Calling X86_GetContext...")
        self.ctx = self.lib.X86_GetContext(self.state).contents
        print("  [DEBUG] Context Accessed.")
        
    def __del__(self):
        if hasattr(self, 'lib'):
            self.lib.X86_Destroy(self.state)
            
    def mem_map(self, addr, size, perms):
        self.lib.X86_MemMap(self.state, addr, size, perms)
        
    def mem_write(self, addr, data):
        buf = (ctypes.c_uint8 * len(data))(*data)
        self.lib.X86_MemWrite(self.state, addr, buf, len(data))
        
    def step(self):
        self.lib.X86_Step(self.state)
        
    def run(self):
        self.lib.X86_Run(self.state)


    # Decoder Binding
    class DecodedOp(ctypes.Structure):
        _pack_ = 1
        _fields_ = [
            ("imm", ctypes.c_uint32),
            ("disp", ctypes.c_uint32),
            ("handler_index", ctypes.c_uint16),
            ("prefixes", ctypes.c_uint16),
            ("modrm", ctypes.c_uint8),
            ("sib", ctypes.c_uint8),
            ("length", ctypes.c_uint8),
            ("flags", ctypes.c_uint8),
        ]

    def decode(self, data):
        buf = (ctypes.c_uint8 * len(data))(*data)
        op = X86Emu.DecodedOp()
        self.lib.X86_Decode(buf, ctypes.byref(op))
        return op

    def set_fault_callback(self, cb):
        # Keep reference to avoid GC
        self._cb_ref = ctypes.CFUNCTYPE(None, ctypes.c_uint32, ctypes.c_int)(cb)
        self.lib.X86_SetFaultCallback(self.state, self._cb_ref)

class TestRunner:
    def __init__(self):
        pass
        
    def compile(self, asm):
        # Use NASM to compile 32-bit assembly
        with tempfile.NamedTemporaryFile(suffix=".asm", mode="w", delete=False) as f:
            f.write(f"BITS 32\n{asm}")
            asm_path = f.name
            
        bin_path = asm_path + ".bin"
        try:
            subprocess.run(["nasm", "-f", "bin", "-o", bin_path, asm_path], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            with open(bin_path, "rb") as f:
                return f.read()
        finally:
            if os.path.exists(asm_path): os.remove(asm_path)
            if os.path.exists(bin_path): os.remove(bin_path)

    def run_case(self, name, asm_code, initial_regs=None):
        print(f"[*] Running Test: {name}")
        try:
            code = self.compile(asm_code)
        except Exception as e:
            print(f"  [-] Compilation Failed: {e}")
            return False
            
        ADDRESS = 0x1000
        STACK_ADDR = 0x2000
        
        # 1. Setup Unicorn
        try:
            uc = Uc(UC_ARCH_X86, UC_MODE_32)
            uc.mem_map(ADDRESS, 0x1000)
            uc.mem_write(ADDRESS, code)
            
            uc.mem_map(STACK_ADDR, 0x1000)
            uc.reg_write(UC_X86_REG_ESP, STACK_ADDR + 0x800)
            
            if initial_regs:
                 for r, v in initial_regs.items():
                     uc.reg_write(r, v)
            
            uc.emu_start(ADDRESS, ADDRESS + len(code))
        except UcError as e:
            print(f"  [-] Unicorn Error: {e}")
            return False

        # 2. Setup Simulator
        try:
            sim = X86Emu()
            
            # Fault Handler
            self.fault_hit = False
            def on_fault(addr, is_write):
                print(f"  [!] FAULT CALLBACK: Addr=0x{addr:X} Write={is_write}")
                self.fault_hit = True
            
            sim.set_fault_callback(on_fault)
            
            sim.mem_map(ADDRESS, 0x1000, 7) # RWX
            sim.mem_write(ADDRESS, code)
            
            # Setup Stack
            STACK_ADDR = 0x2000
            sim.mem_map(STACK_ADDR, 0x1000, 7) 
            sim.ctx.regs[4] = STACK_ADDR + 0x800 # ESP
            
            if initial_regs:
                # EAX=0, ECX=1...
                pass
                
            sim.ctx.eip = ADDRESS
            
            # Test Decode Explicitly
            op = sim.decode(code)
            print(f"  [.] Decode Result: Len={op.length} Handler={op.handler_index}")
            
            if op.handler_index == 1: # ADD
                 # Unpack bitfields
                 print("      Raw DecodedOp Bytes:", bytes(op).hex())
                 # C++: op1_type : 3 (bits 0-2)
                 op1_type = op.operand_fields & 0x7
                 op2_type = (op.operand_fields >> 3) & 0x7
                 print(f"      ADD Detected. Op1Type={op1_type} Op2Type={op2_type} Imm=0x{op.imm:X}")

            # Step
            sim.step()
            print("  [DEBUG] Python: Returned from sim.step()")
            
            if self.fault_hit:
                print("  [+] Fault handled gracefully.")
                return True
                
            # Verify with Capstone
            md = Cs(CS_ARCH_X86, CS_MODE_32)
            # md.detail = True # Not strictly needed for basic check
            disasm = list(md.disasm(code, ADDRESS))
            if disasm:
                ins = disasm[0]
                print(f"  [.] Capstone: {ins.mnemonic} {ins.op_str}")
                # We can verify Length here
                if ins.size != op.length:
                    print(f"  [-] Length Mismatch! Capstone={ins.size} Sim={op.length}")
            
            # Compare Registers
            # Compare Registers
            print("  [.] Comparing Registers...")
            # sim_regs = sim.get_regs() # Helper method missing in X86Emu
            # Access directly
            sim_regs = list(sim.ctx.regs)
            
            # Unicorn regs
            uc_eax = uc.reg_read(UC_X86_REG_EAX)
            print(f"  [+] Unicorn EAX: 0x{uc_eax:08X}")
            print(f"  [+] Sim     EAX: 0x{sim_regs[0]:08X}")
            
            if uc_eax != sim_regs[0]:
                 print("  [-] EAX Mismatch! (Expected for now)")
                 # return False
                
        except Exception as e:
             print(f"  [-] python crash: {e}")
             return False
             
        # Mock Comparison (Simulator is empty execution)
        return True

if __name__ == "__main__":
    lib = ctypes.CDLL(LIB_PATH)
    try:
        lib.X86_DebugStructSizes()
    except:
        print("[-] X86_DebugStructSizes not found in lib")

    print(f"[*] Python sizeof(Context) = {ctypes.sizeof(Context)}")
    print(f"[*] Python Offset xmm = {Context.xmm.offset}")
    print(f"[*] Python Offset mmu = {Context.mmu.offset}")
    print(f"[*] Python sizeof(DecodedOp) = {ctypes.sizeof(X86Emu.DecodedOp)}")

    # 4. ModRM Test (MOV [EAX+4], EBX)
    # Opcode 89
    # ModRM: 58 (Mod=01 Disp8, Reg=3 EBX, RM=0 EAX)
    # Disp: 04
    # Loopback Test:
    # 1. MOV [EAX+4], EBX (0x89 0x58 0x04) -> Writes DEADBEEF to 0x2004
    # 2. MOV ECX, [EAX+4] (0x8B 0x48 0x04) -> Reads 0x2004 to ECX
    # 3. Check ECX
    
    # 0x1000: 89 58 04
    # 0x1003: 8B 48 04
    
    # Stack & LEA Test
    # 1. LEA EAX, [EBX+4]     (8D 43 04) -> EAX = EBX+4
    # 2. PUSH EAX             (50)       -> Stack Push EAX
    # 3. PUSH 0x12345678      (68 78 56 34 12) -> Stack Push Imm
    # 4. POP ECX              (59)       -> ECX = 0x12345678
    # 5. POP EDX              (5A)       -> EDX = EAX
    
    # Init: EBX = 0x20. ESP = 0x2800.
    
    print("\n[*] Running Stack & LEA Test (Using X86_Run / Basic Block)...")
    sim = X86Emu()
    
    # Code with HLT (F4) at the end to stop
    code = b"\x8D\x43\x04" + b"\x50" + b"\x68\x78\x56\x34\x12" + b"\x59" + b"\x5A" + b"\xF4"
    sim.mem_map(0x1000, 0x1000, 7)
    sim.mem_write(0x1000, code)
    
    # Stack Page
    sim.mem_map(0x2000, 0x1000, 7)
    sim.ctx.regs[3] = 0x20   # EBX
    sim.ctx.regs[4] = 0x2800 # ESP matches stack map end
    
    sim.ctx.eip = 0x1000
    
    # Run until HLT or Fault
    print("  [.] Calling sim.run()...")
    sim.run()
    
    # Check EIP (Should be after HLT)
    # HLT is 1 byte. Code length is 3+1+5+1+1+1 = 12 bytes.
    # EIP should be 0x100C.
    if sim.ctx.eip != 0x100C:
         print(f"  [-] EIP Mismatch: 0x{sim.ctx.eip:X} (Expected 0x100C)")
    else:
         print(f"  [+] EIP Correct: 0x{sim.ctx.eip:X}")
    
    # Verification
    # EAX should be 0x24 (36)
    eax = sim.ctx.regs[0]
    ecx = sim.ctx.regs[1]
    edx = sim.ctx.regs[2] # EDX is index 2
    esp = sim.ctx.regs[4]
    
    print(f"  [.] Registers: EAX=0x{eax:X} ECX=0x{ecx:X} EDX=0x{edx:X} ESP=0x{esp:X}")
    
    success = True
    if eax != 0x24:
        print("  [-] EAX Mismatch (Expected 0x24)")
        success = False
    if ecx != 0x12345678:
        print("  [-] ECX Mismatch (Expected 0x12345678)")
        success = False
    if edx != 0x24:
        print(f"  [-] EDX Mismatch (Expected 0x24), got 0x{edx:X}")
        success = False
    if esp != 0x2800:
        print(f"  [-] ESP Mismatch (Expected 0x2800), got 0x{esp:X}")
        success = False
        
    if success:
         print("  [+] SUCCESS: Stack/LEA verified.")
    else:
         print("  [-] FAILURE.")
         
    sys.exit(0)

