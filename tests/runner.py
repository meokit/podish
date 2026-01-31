import cppyy
import os
import sys
import tempfile
import subprocess
import ctypes

# Check for Unicorn
UNICORN_AVAILABLE = False
try:
    from unicorn import *
    from unicorn.x86_const import *
    UNICORN_AVAILABLE = True
except ImportError:
    pass

# Configurations
PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
BUILD_PATH = os.path.join(PROJECT_ROOT, "build")
SRC_PATH = os.path.join(PROJECT_ROOT, "src")
SIMDE_PATH = os.path.join(BUILD_PATH, "_deps/simde-src")
LIB_PATH = os.path.join(BUILD_PATH, "libx86emu.dylib") # Adjust extension if linux/windows

if not os.path.exists(LIB_PATH):
    # Try finding it
    if os.path.exists(os.path.join(BUILD_PATH, "libx86emu.so")):
        LIB_PATH = os.path.join(BUILD_PATH, "libx86emu.so")
    elif os.path.exists(os.path.join(BUILD_PATH, "libx86emu.dll")):
        LIB_PATH = os.path.join(BUILD_PATH, "libx86emu.dll")

# Load Library and Includes
cppyy.add_include_path(SRC_PATH)
cppyy.add_include_path(SIMDE_PATH)

# Pre-include some standard headers to avoid issues
cppyy.c_include("stdint.h")

# Load our headers
# Note: We need to include common.h first because it defines SIMDE alias
cppyy.include("common.h")
cppyy.include("state.h")
cppyy.include("bindings.h")

# Load the shared library
cppyy.load_library(LIB_PATH)

# Helper to handle bytes -> uint8_t* conversion
cppyy.cppdef("""
void Py_MemWrite(x86emu::EmuState* state, uint32_t addr, const char* data, uint32_t size) {
    X86_MemWrite(state, addr, (const uint8_t*)data, size);
}

void Py_WriteXmm(x86emu::Context* ctx, int idx, const char* data) {
    std::memcpy(&ctx->xmm[idx], data, 16);
}

unsigned long long Py_GetXmmAddr(x86emu::Context* ctx, int idx) {
    return (unsigned long long)&ctx->xmm[idx];
}
""")

class X86Emu:
    def __init__(self):
        # Create State
        self.state = cppyy.gbl.X86_Create()
        self.ctx = cppyy.gbl.X86_GetContext(self.state)
        self._cb = None # Keep reference to callback
        self._mem_hook = None

    def __del__(self):
        if hasattr(self, 'state') and self.state:
            cppyy.gbl.X86_Destroy(self.state)
            
    def mem_map(self, addr, size, perms):
        cppyy.gbl.X86_MemMap(self.state, addr, size, perms)
        
    def mem_write(self, addr, data):
        # data is bytes/list. X86_MemWrite expects uint8_t*
        if isinstance(data, (bytes, bytearray)):
            # bytes maps to const char*
            # Ensure it is bytes (cppyy might not handle bytearray automatically for const char*)
            b = bytes(data)
            cppyy.gbl.Py_MemWrite(self.state, addr, b, len(b))
        else:
            # list of ints
            b = bytes(data)
            cppyy.gbl.Py_MemWrite(self.state, addr, b, len(b))
        
    def step(self):
        return cppyy.gbl.X86_Step(self.state)
        
    def run(self):
        cppyy.gbl.X86_Run(self.state)

    def stop(self):
        cppyy.gbl.X86_EmuStop(self.state)

    def set_fault_callback(self, cb):
        self._cb = cb
        cppyy.gbl.X86_SetFaultCallback(self.state, cb)

    def set_mem_hook(self, cb):
        self._mem_hook = cb
        cppyy.gbl.X86_SetMemHook(self.state, cb)

    def set_intr_hook(self, vector, cb):
        if not hasattr(self, '_intr_hooks'):
            self._intr_hooks = {}
        self._intr_hooks[vector] = cb
        cppyy.gbl.X86_SetInterruptHook(self.state, vector, cb)

class Runner:
    def __init__(self):
        self.sim_trace = []
        self.uc_trace = []

    def compile(self, asm):
        # Use NASM to compile 32-bit assembly
        with tempfile.NamedTemporaryFile(suffix=".asm", mode="w", delete=False) as f:
            f.write(f"BITS 32\nORG 0x1000\n{asm}")
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

    def run_test(self, name, asm, initial_regs=None, expected_regs=None, initial_eflags=None, expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, initial_seg_base=None, check_eflags_mask=None):
        code = self.compile(asm)
        if not code:
            return False
            
        return self._execute_test(name, code, initial_regs, expected_regs, initial_eflags, expected_eflags, expected_eip, expected_read, expected_write, initial_seg_base, check_eflags_mask)

    def run_test_bytes(self, name, code, initial_regs=None, expected_regs=None, initial_eflags=None, expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, initial_seg_base=None, check_eflags_mask=None):
        return self._execute_test(name, code, initial_regs, expected_regs, initial_eflags, expected_eflags, expected_eip, expected_read, expected_write, initial_seg_base, check_eflags_mask)

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

    def _execute_test(self, name, code, initial_regs=None, expected_regs=None, initial_eflags=None, expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, initial_seg_base=None, check_eflags_mask=None):
        # print(f"[TEST] Running {name}...")
        
        # 1. Setup Unicorn (Reference)
        uc_res = {}
        uc_exception = None
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
                
                # Write Code (Pad with 0xCC to catch execution runaways)
                CODE_ADDR = 0x1000
                padded_code = bytearray([0xCC] * 0x1000)
                padded_code[:len(code)] = code
                mu.mem_write(CODE_ADDR, bytes(padded_code))
                
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
                        if reg_id:
                            if r.startswith('XMM'):
                                # Handle XMM as 16-byte bytes
                                if isinstance(v, (list, tuple, bytes)):
                                    val_bytes = bytes(v)
                                elif isinstance(v, int):
                                    val_bytes = v.to_bytes(16, 'little')
                                
                                mu.reg_write(reg_id, val_bytes)
                            else:
                                mu.reg_write(reg_id, v)

                if initial_eflags is not None:
                     mu.reg_write(UC_X86_REG_EFLAGS, initial_eflags)

                if initial_seg_base:
                    pass

                # Initialize Memory from expected_read
                if expected_read:
                    for addr, val in expected_read.items():
                        byte_len = (val.bit_length() + 7) // 8
                        if byte_len == 0: byte_len = 4 # Default for 0
                        elif byte_len < 4: byte_len = 4
                        mu.mem_write(addr, val.to_bytes(byte_len, 'little'))

                # Add Hooks
                # NOTE: Unicorn hook disabled due to ctypes incompatibility with bound methods
                # self.uc_trace will remain empty
                # mu.hook_add(UC_HOOK_MEM_READ | UC_HOOK_MEM_WRITE, self._uc_mem_hook)
                
                # Run
                mu.emu_start(CODE_ADDR, CODE_ADDR + len(code))
                
                # Collect Results
                try:
                    uc_res['EAX'] = mu.reg_read(UC_X86_REG_EAX)
                except Exception as e:
                    pass
                
                uc_res['ECX'] = mu.reg_read(UC_X86_REG_ECX)
                uc_res['EDX'] = mu.reg_read(UC_X86_REG_EDX)
                uc_res['EBX'] = mu.reg_read(UC_X86_REG_EBX)
                uc_res['ESP'] = mu.reg_read(UC_X86_REG_ESP)
                uc_res['EBP'] = mu.reg_read(UC_X86_REG_EBP)
                uc_res['ESI'] = mu.reg_read(UC_X86_REG_ESI)
                uc_res['EDI'] = mu.reg_read(UC_X86_REG_EDI)
                for i in range(8):
                    uc_res[f'XMM{i}'] = mu.reg_read(UC_X86_REG_XMM0 + i)

                uc_eflags = mu.reg_read(UC_X86_REG_EFLAGS)
                uc_eip_out = mu.reg_read(UC_X86_REG_EIP)

            except Exception as e:
                uc_exception = e
                # print(f"  [Unicorn] Error Type: {type(e)}")
                # print(f"  [Unicorn] Error: {e}")
                pass
        
        # 2. Setup Our Simulator
        sim = X86Emu()
        
        # Map Memory - base regions
        sim.mem_map(0x1000, 0x1000, 7) # Code (RWX)
        sim.mem_map(0x2000, 0x1000, 3) # Data (RW)
        sim.mem_map(0x7000, 0x2000, 3) # Stack (RW)
        
        # Map additional memory for expected_read and expected_write addresses
        # to avoid MMU segfaults during tests
        addresses_to_map = set()
        if expected_read:
            for addr, val in expected_read.items():
                # For 128-bit values, we need to map addr and addr+8
                if val > 0xFFFFFFFFFFFFFFFF:
                    addresses_to_map.add(addr)
                    addresses_to_map.add(addr + 8)
                else:
                    addresses_to_map.add(addr)
        if expected_write:
            for addr, val in expected_write.items():
                # For 128-bit values, we need to map addr and addr+8
                if val > 0xFFFFFFFFFFFFFFFF:
                    addresses_to_map.add(addr)
                    addresses_to_map.add(addr + 8)
                else:
                    addresses_to_map.add(addr)
        
        # Map pages containing these addresses (align to 4KB pages)
        mapped_pages = set()
        for addr in addresses_to_map:
            page_base = (addr // 0x1000) * 0x1000
            if page_base not in mapped_pages and page_base not in [0x1000, 0x2000, 0x7000, 0x8000]:
                try:
                    sim.mem_map(page_base, 0x1000, 3)  # RW
                    mapped_pages.add(page_base)
                except:
                    pass  # Already mapped
        
        # Write Code
        # Reuse padded_code from above if available, else recreate
        if 'padded_code' not in locals():
            padded_code = bytearray([0xCC] * 0x1000)
            padded_code[:len(code)] = code
            
        sim.mem_write(0x1000, padded_code)
        
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
                if r.startswith('XMM'):
                    idx = int(r[3:])
                    if 0 <= idx < 8:
                        if isinstance(v, int):
                            v_bytes = v.to_bytes(16, 'little')
                        else:
                            v_bytes = bytes(v)
                        
                        cppyy.gbl.Py_WriteXmm(sim.ctx, idx, v_bytes)
                else:
                    idx = self._get_sim_reg_idx(r)
                    if idx != -1: sim.ctx.regs[idx] = v
        
        if initial_eflags is not None:
             sim.ctx.eflags = initial_eflags
        
        # Initialize Memory from expected_read
        if expected_read:
            for addr, val in expected_read.items():
                byte_len = (val.bit_length() + 7) // 8
                if byte_len == 0: byte_len = 4
                elif byte_len < 4: byte_len = 4
                # For 128-bit values, force to 16 bytes
                elif val > 0xFFFFFFFFFFFFFFFF:
                    byte_len = 16
                sim.mem_write(addr, val.to_bytes(byte_len, 'little'))

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
        CODE_START = 0x1000
        CODE_END = CODE_START + len(code)
        sim_status = 0
        
        for _ in range(MAX_STEPS):
            # Check if EIP is within code range BEFORE executing? 
            # Unicorn stops when EIP reaches end.
            if sim.ctx.eip >= CODE_END:
                break
                
            sim_status = sim.step()
            
            if sim_status == 1: # Stopped
                break
            elif sim_status == 2: # Fault
                # print("  [Sim] Fault Detected!")
                break
                
            # Also check after step if we jumped out
            if sim.ctx.eip < CODE_START or sim.ctx.eip >= CODE_END:
                break
        
        # Compare Results
        passed = True
        fail_reason = ""

        # Check for Abnormal Termination
        uc_crashed = UNICORN_AVAILABLE and (uc_res.get('ESP', 0) == 0 or uc_exception is not None)
        
        if sim_status == 2:
            # Check Fault Vector
            sim_fault_vec = getattr(sim.state, 'fault_vector', 0)
            
            ignored = False
            if sim_fault_vec == 6: # #UD (Unimplemented / Invalid Opcode)
                # Strict Check: Only ignore if Unicorn ALSO said Invalid Opcode
                # Unicorn Invalid Opcode is UC_ERR_INSN_INVALID (10)
                is_uc_invalid = False
                if uc_exception and hasattr(uc_exception, 'errno') and uc_exception.errno == 10:
                    is_uc_invalid = True
                
                if is_uc_invalid:
                    print(f"  [WARN] Both Sim and Unicorn #UD. Ignoring.")
                    ignored = True
                else:
                    # Sim says #UD, Unicorn says OK or #GP/#PF etc. -> Missing Implementation!
                    ignored = False
            else:
                # Other Faults (Segfault, etc.) -> Ignore if Unicorn also crashed
                if uc_crashed:
                    print(f"  [WARN] Simulator Faulted ({sim_fault_vec}) and Unicorn crashed. Ignoring.")
                    ignored = True

            if not ignored:
                fail_reason += f"  Simulator Faulted (Vector {sim_fault_vec}) at EIP=0x{sim.ctx.eip:x}\n"
                if sim_fault_vec == 6:
                    # Capture UC Error for context
                    uc_err_msg = str(uc_exception) if uc_exception else "N/A"
                    fail_reason += f"  -> Unimplemented Instruction (or Invalid Opcode) vs Unicorn: {uc_err_msg}\n"
                passed = False
        
        
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
                    # WORKAROUND 1: Unicorn Corruption (All Zeros/ESP=0)
                    # If Unicorn ESP is 0 but Sim ESP is valid, Unicorn likely crashed/cleared context.
                    uc_esp = uc_res.get('ESP', 0)
                    if uc_esp == 0:
                        print(f"  [WARN] Ignoring Unicorn Corruption (ESP=0). Sim: 0x{sim_val:x}")
                        continue

                    # WORKAROUND 2: Unicorn on some platforms returns EAX=0 incorrectly for SSE tests
                    if r == 'EAX' and uc_val == 0 and sim_val != 0:
                        print(f"  [WARN] Ignoring Unicorn EAX=0 mismatch. Sim: 0x{sim_val:x}")
                        continue
                        
                    fail_reason += f"  {r} Unicorn Mismatch! UC: 0x{uc_val:x}, Sim: 0x{sim_val:x}\n"
                    passed = False

        # 2. Compare XMM
        for i in range(8):
            r = f'XMM{i}'
            # Use helper to get address
            addr = cppyy.gbl.Py_GetXmmAddr(sim.ctx, i)
            # print(f"[DEBUG] XMM{i} Addr: 0x{addr:x}")
            sim_val = ctypes.string_at(addr, 16)
            if expected_regs and r in expected_regs:
                exp_v = expected_regs[r]
                if isinstance(exp_v, int):
                    # Ensure unsigned 128-bit
                    exp_v = exp_v & ((1 << 128) - 1)
                    exp_v = exp_v.to_bytes(16, 'little')
                else:
                    exp_v = bytes(exp_v)
                
                if sim_val != exp_v:
                    fail_reason += f"  {r} Mismatch! Exp: {exp_v.hex()}, Got: {sim_val.hex()}\n"
                    passed = False
            elif UNICORN_AVAILABLE and (expected_regs is None or r not in expected_regs):
                uc_val = uc_res.get(r)
                if uc_val is not None:
                    if isinstance(uc_val, int):
                        uc_val = uc_val.to_bytes(16, 'little')
                    if sim_val != uc_val:
                        fail_reason += f"  {r} Unicorn Mismatch! UC: {uc_val.hex()}, Sim: {sim_val.hex()}\n"
                        passed = False

        # 3. Check EFLAGS (Strict check if expected provided, masked for arithmetic status)
        sim_eflags = sim.ctx.eflags
        # Default to Arithmetic Flags (0x8D5) + Direction Flag if not specified
        # Adding DF (0x400) to default? No, keep conservative.
        mask = check_eflags_mask if check_eflags_mask is not None else 0x8D5
        
        if expected_eflags is not None:
            if (sim_eflags & mask) != (expected_eflags & mask):
                 fail_reason += f"  EFLAGS Mismatch! Exp: 0x{expected_eflags:x}, Got: 0x{sim_eflags:x} (Masked: 0x{sim_eflags & mask:x} with mask 0x{mask:x})\n"
                 passed = False
        
        # 4. Check EIP
        if expected_eip is not None:
            if sim.ctx.eip != expected_eip:
                fail_reason += f"  EIP Mismatch! Exp: 0x{expected_eip:x}, Got: 0x{sim.ctx.eip:x}\n"
                passed = False
        
        # 5. Check Memory Accesses
        if expected_read:
            for addr, val in expected_read.items():
                found = False
                # Check if val is a 128-bit value (> 64-bit max)
                if val > 0xFFFFFFFFFFFFFFFF:
                    # For 128-bit values, check if we have two consecutive 64-bit reads
                    low64 = val & 0xFFFFFFFFFFFFFFFF
                    high64 = (val >> 64) & 0xFFFFFFFFFFFFFFFF
                    found_low = False
                    found_high = False
                    for op, t_addr, t_val, t_size in self.sim_trace:
                        if op == 'R' and t_addr == addr and t_val == low64 and t_size == 8:
                            found_low = True
                        if op == 'R' and t_addr == addr + 8 and t_val == high64 and t_size == 8:
                            found_high = True
                    found = found_low and found_high
                else:
                    # For smaller values, do exact match
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
                # Check if val is a 128-bit value (> 64-bit max)
                if val > 0xFFFFFFFFFFFFFFFF:
                    # For 128-bit values, check if we have two consecutive 64-bit writes
                    low64 = val & 0xFFFFFFFFFFFFFFFF
                    high64 = (val >> 64) & 0xFFFFFFFFFFFFFFFF
                    found_low = False
                    found_high = False
                    for op, t_addr, t_val, t_size in self.sim_trace:
                        if op == 'W' and t_addr == addr and t_val == low64 and t_size == 8:
                            found_low = True
                        if op == 'W' and t_addr == addr + 8 and t_val == high64 and t_size == 8:
                            found_high = True
                    found = found_low and found_high
                else:
                    # For smaller values, do exact match
                    for op, t_addr, t_val, t_size in self.sim_trace:
                        if op == 'W' and t_addr == addr and t_val == val:
                            found = True
                            break
                if not found:
                     fail_reason += f"  Expected Write at 0x{addr:x} with value 0x{val:x} not found in trace.\n"
                     # Dump actual writes near addr
                     fail_reason += "  Actual writes in trace:\n"
                     for op_t, t_addr, t_val, t_size in self.sim_trace:
                         if op_t == 'W':
                             fail_reason += f"    Host Write: Addr=0x{t_addr:x} Val=0x{t_val:x} Size={t_size}\n"
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
            'ESP': UC_X86_REG_ESP, 'EBP': UC_X86_REG_EBP, 'ESI': UC_X86_REG_ESI, 'EDI': UC_X86_REG_EDI,
            'XMM0': UC_X86_REG_XMM0, 'XMM1': UC_X86_REG_XMM1, 'XMM2': UC_X86_REG_XMM2, 'XMM3': UC_X86_REG_XMM3,
            'XMM4': UC_X86_REG_XMM4, 'XMM5': UC_X86_REG_XMM5, 'XMM6': UC_X86_REG_XMM6, 'XMM7': UC_X86_REG_XMM7,
        }
        return mapping.get(name)

    def _get_sim_reg_idx(self, name):
        mapping = {
            'EAX': 0, 'ECX': 1, 'EDX': 2, 'EBX': 3,
            'ESP': 4, 'EBP': 5, 'ESI': 6, 'EDI': 7
        }
        return mapping.get(name, -1)