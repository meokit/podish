# /// script
# dependencies = []
# ///

import ctypes
import os
import sys
import struct
import subprocess
import tempfile

try:
    from unicorn import *
    from unicorn.x86_const import *
    UNICORN_AVAILABLE = True
except ImportError:
    UNICORN_AVAILABLE = False
    print("[-] Unicorn Engine not installed. Verification disabled.")

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
        self.lib.X86_Step.restype = ctypes.c_int
        self.lib.X86_Step.argtypes = [ctypes.c_void_p]
        self.lib.X86_Run.argtypes = [ctypes.c_void_p]
        self.lib.X86_EmuStop.argtypes = [ctypes.c_void_p]
        self.lib.X86_Destroy.argtypes = [ctypes.c_void_p]

        # Memory Hook Binding
        self.lib.X86_SetMemHook.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        
        # print("  [DEBUG] Calling X86_Create...")
        self.state = self.lib.X86_Create()
        # print(f"  [DEBUG] State Ptr: {self.state}")
        
        # print("  [DEBUG] Calling X86_GetContext...")
        self.ctx = self.lib.X86_GetContext(self.state).contents
        # print("  [DEBUG] Context Accessed.")
        
    def __del__(self):
        if hasattr(self, 'lib'):
            self.lib.X86_Destroy(self.state)
            
    def mem_map(self, addr, size, perms):
        self.lib.X86_MemMap(self.state, addr, size, perms)
        
    def mem_write(self, addr, data):
        buf = (ctypes.c_uint8 * len(data))(*data)
        self.lib.X86_MemWrite(self.state, addr, buf, len(data))
        
    def step(self):
        return self.lib.X86_Step(self.state)
        
    def run(self):
        self.lib.X86_Run(self.state)

    def stop(self):
        self.lib.X86_EmuStop(self.state)

    def set_fault_callback(self, cb):
        self._cb_ref = ctypes.CFUNCTYPE(None, ctypes.c_uint32, ctypes.c_int)(cb)
        self.lib.X86_SetFaultCallback(self.state, self._cb_ref)

    def set_mem_hook(self, cb):
        # void(*)(uint32_t addr, uint32_t size, int is_write, uint64_t val)
        self._mem_hook_ref = ctypes.CFUNCTYPE(None, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int, ctypes.c_uint64)(cb)
        self.lib.X86_SetMemHook(self.state, self._mem_hook_ref)

class Runner:
    def __init__(self):
        self.sim_trace = []
        self.uc_trace = []

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
        except FileNotFoundError:
             raise Exception("NASM not found. Please install nasm.")
        except subprocess.CalledProcessError as e:
             raise Exception(f"NASM Conversion Failed: {e}")
        finally:
            if os.path.exists(asm_path): os.remove(asm_path)
            if os.path.exists(bin_path): os.remove(bin_path)

    def run_test(self, name, asm, initial_regs=None, expected_regs=None, initial_eflags=None, expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, initial_seg_base=None):
        code = self.compile(asm)
        if not code:
            return False
            
        return self._execute_test(name, code, initial_regs, expected_regs, initial_eflags, expected_eflags, expected_eip, expected_read, expected_write, initial_seg_base)

    def run_test_bytes(self, name, code, initial_regs=None, expected_regs=None, initial_eflags=None, expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, initial_seg_base=None):
        return self._execute_test(name, code, initial_regs, expected_regs, initial_eflags, expected_eflags, expected_eip, expected_read, expected_write, initial_seg_base)

    def _sim_mem_hook(self, addr, size, is_write, val):
        # Store as (Type, Addr, Val, Size)
        # Type: 'W' or 'R'
        op = 'W' if is_write else 'R'
        self.sim_trace.append((op, addr, val, size))

    def _uc_mem_hook(self, uc, access, address, size, value, user_data):
        op = 'W' if access == UC_MEM_WRITE else 'R'
        # For Read, value IS NOT PASSED by Unicorn correctly in the hook (it's often 0 or undefined BEFORE the read).
        real_val = value
        if op == 'R':
            try:
                data = uc.mem_read(address, size)
                real_val = int.from_bytes(data, 'little')
            except:
                real_val = 0
        self.uc_trace.append((op, address, real_val, size))

    def _execute_test(self, name, code, initial_regs=None, expected_regs=None, initial_eflags=None, expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, initial_seg_base=None):
        # print(f"[TEST] Running {name}...")
        
        # 1. Setup Unicorn (Reference)
        uc_res = {}
        uc_eflags = 0
        uc_eip_out = 0
        self.uc_trace = []
        
        if UNICORN_AVAILABLE:
            try:
                mu = Uc(UC_ARCH_X86, UC_MODE_32)
                
                # Map Access
                mu.mem_map(0x1000, 0x1000) # Code
                mu.mem_map(0x2000, 0x1000) # Data
                mu.mem_map(0x7000, 0x2000) # Stack (0x7000-0x9000), ESP at 0x8000
                
                # Write Code
                CODE_ADDR = 0x1000
                mu.mem_write(CODE_ADDR, code)
                
                # Setup Regs
                mu.reg_write(UC_X86_REG_EAX, 1)
                mu.reg_write(UC_X86_REG_ECX, 2)
                mu.reg_write(UC_X86_REG_EDX, 3)
                mu.reg_write(UC_X86_REG_EBX, 4)
                mu.reg_write(UC_X86_REG_ESP, 0x8000)
                mu.reg_write(UC_X86_REG_EBP, 0x100)
                mu.reg_write(UC_X86_REG_ESI, 5)
                mu.reg_write(UC_X86_REG_EDI, 6)
                mu.reg_write(UC_X86_REG_EFLAGS, 0x202) # Standard (IF=1, Reserved=1)
                
                if initial_regs:
                    for r, v in initial_regs.items():
                        reg_id = self._get_uc_reg(r)
                        if reg_id: mu.reg_write(reg_id, v)

                if initial_eflags is not None:
                     mu.reg_write(UC_X86_REG_EFLAGS, initial_eflags)

                if initial_seg_base:
                    # Unicorn doesn't easily support segment base modification in 32-bit mode
                    # but we document it here for completeness
                    # seg_base: [ES, CS, SS, DS, FS, GS]
                    pass

                # Add Hooks
                mu.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, self._uc_mem_hook)
                
                # Run
                mu.emu_start(CODE_ADDR, CODE_ADDR + len(code))
                
                # Collect Results
                uc_res['EAX'] = mu.reg_read(UC_X86_REG_EAX)
                uc_res['ECX'] = mu.reg_read(UC_X86_REG_ECX)
                uc_res['EDX'] = mu.reg_read(UC_X86_REG_EDX)
                uc_res['EBX'] = mu.reg_read(UC_X86_REG_EBX)
                uc_res['ESP'] = mu.reg_read(UC_X86_REG_ESP)
                uc_res['EBP'] = mu.reg_read(UC_X86_REG_EBP)
                uc_res['ESI'] = mu.reg_read(UC_X86_REG_ESI)
                uc_res['EDI'] = mu.reg_read(UC_X86_REG_EDI)
                uc_eflags = mu.reg_read(UC_X86_REG_EFLAGS)
                uc_eip_out = mu.reg_read(UC_X86_REG_EIP)

            except Exception as e:
                # print(f"  [Unicorn] Error: {e}")
                pass
        
        # 2. Setup Our Simulator
        sim = X86Emu()
        
        # Map Memory
        sim.mem_map(0x1000, 0x1000, 7) # Code (RWX)
        sim.mem_map(0x2000, 0x1000, 3) # Data (RW)
        sim.mem_map(0x7000, 0x2000, 3) # Stack (RW)
        
        # Write Code
        sim.mem_write(0x1000, code)
        
        # Defaults
        sim.ctx.regs[0] = 1 # EAX
        sim.ctx.regs[1] = 2 # ECX
        sim.ctx.regs[2] = 3 # EDX
        sim.ctx.regs[3] = 4 # EBX
        sim.ctx.regs[4] = 0x8000 # ESP
        sim.ctx.regs[5] = 0x100 # EBP
        sim.ctx.regs[6] = 5 # ESI
        sim.ctx.regs[7] = 6 # EDI
        sim.ctx.eflags = 0x202 # Standard
        sim.ctx.eip = 0x1000

        if initial_regs:
             for r, v in initial_regs.items():
                idx = self._get_sim_reg_idx(r)
                if idx != -1: sim.ctx.regs[idx] = v
        
        if initial_eflags is not None:
             sim.ctx.eflags = initial_eflags
        
        # Set segment bases (defaults to 0 for flat model)
        if initial_seg_base:
            for i, base in enumerate(initial_seg_base):
                if i < 6:  # ES, CS, SS, DS, FS, GS
                    sim.ctx.seg_base[i] = base
        
        # Setup Trace
        self.sim_trace = []
        sim.set_mem_hook(self._sim_mem_hook)

        # Step Loop
        MAX_STEPS = 50
        for _ in range(MAX_STEPS):
            status = sim.step()
            if status == 1: # Stopped
                break
            elif status == 2: # Fault
                # print("  [Sim] Fault Detected!")
                break
        
        # Compare Results
        passed = True
        fail_reason = ""
        
        # 1. Compare Registers (Basic)
        reg_names = ['EAX', 'ECX', 'EDX', 'EBX', 'ESP', 'EBP', 'ESI', 'EDI']
        for i, r in enumerate(reg_names):
            sim_val = sim.ctx.regs[i]
            
            # Check against Expected
            if expected_regs and r in expected_regs:
                if sim_val != expected_regs[r]:
                    fail_reason += f"  {r} Mismatch! Exp: 0x{expected_regs[r]:x}, Got: 0x{sim_val:x}\n"
                    passed = False
            # Check against Unicorn (if available and no manual expectation override)
            elif UNICORN_AVAILABLE and (expected_regs is None or r not in expected_regs):
                uc_val = uc_res.get(r, 0)
                if sim_val != uc_val:
                    fail_reason += f"  {r} Unicorn Mismatch! UC: 0x{uc_val:x}, Sim: 0x{sim_val:x}\n"
                    passed = False

        # 2. Check EFLAGS (Strict check if expected provided)
        sim_eflags = sim.ctx.eflags
        if expected_eflags is not None:
            if sim_eflags != expected_eflags:
                 fail_reason += f"  EFLAGS Mismatch! Exp: 0x{expected_eflags:x}, Got: 0x{sim_eflags:x}\n"
                 passed = False
        
        # 3. Check EIP
        if expected_eip is not None:
            if sim.ctx.eip != expected_eip:
                fail_reason += f"  EIP Mismatch! Exp: 0x{expected_eip:x}, Got: 0x{sim.ctx.eip:x}\n"
                passed = False
        
        # 4. Check Memory Accesses
        if expected_read:
            for addr, val in expected_read.items():
                found = False
                for op, t_addr, t_val, t_size in self.sim_trace:
                    if op == 'R' and t_addr == addr and t_val == val:
                        found = True
                        break
                if not found:
                     fail_reason += f"  Expected Read at 0x{addr:x} with value 0x{val:x} not found in trace.\n"
                     passed = False

        if expected_write:
            for addr, val in expected_write.items():
                found = False
                for op, t_addr, t_val, t_size in self.sim_trace:
                    if op == 'W' and t_addr == addr and t_val == val:
                        found = True
                        break
                if not found:
                     fail_reason += f"  Expected Write at 0x{addr:x} with value 0x{val:x} not found in trace.\n"
                     passed = False

        if passed:
            print(f"[PASS] {name}")
            return True
        else:
            print(f"[FAIL] {name}")
            print(fail_reason)
            raise AssertionError(f"Test '{name}' failed:\n{fail_reason}")

    def _get_uc_reg(self, name):
        mapping = {
            'EAX': UC_X86_REG_EAX, 'ECX': UC_X86_REG_ECX, 'EDX': UC_X86_REG_EDX, 'EBX': UC_X86_REG_EBX,
            'ESP': UC_X86_REG_ESP, 'EBP': UC_X86_REG_EBP, 'ESI': UC_X86_REG_ESI, 'EDI': UC_X86_REG_EDI
        }
        return mapping.get(name)

    def _get_sim_reg_idx(self, name):
        mapping = {
            'EAX': 0, 'ECX': 1, 'EDX': 2, 'EBX': 3,
            'ESP': 4, 'EBP': 5, 'ESI': 6, 'EDI': 7
        }
        return mapping.get(name, -1)
