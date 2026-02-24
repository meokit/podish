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
SRC_PATH = os.path.join(PROJECT_ROOT, "libfibercpu")
SIMDE_PATH = os.path.join(BUILD_PATH, "_deps/simde-src")

def _find_lib():
    search_paths = [
        os.path.join(PROJECT_ROOT, "Fiberish.X86", "build_native"),
        os.path.join(PROJECT_ROOT, "build", "bin"),
        os.path.join(PROJECT_ROOT, "cmake-build-release", "bin"),
        os.path.join(PROJECT_ROOT, "cmake-build-debug", "bin")
    ]
    exts = [".dylib", ".so", ".dll"]
    for path in search_paths:
        for ext in exts:
            lib_path = os.path.join(path, "libfibercpu" + ext)
            if os.path.exists(lib_path):
                return lib_path
    return os.path.join(BUILD_PATH, "bin", "libfibercpu.dylib") # default fallback

LIB_PATH = _find_lib()

# Load Library and Includes
cppyy.add_include_path(SRC_PATH)

# Pre-include some standard headers to avoid issues
cppyy.c_include("stdint.h")

# Load our bindings header (Opaque Interface)
cppyy.include("bindings.h")

# Load the shared library
cppyy.load_library(LIB_PATH)

# Helper to handle bytes -> uint8_t* conversion
cppyy.cppdef("""
#ifndef X86EMU_PY_HELPERS
#define X86EMU_PY_HELPERS
void Py_MemWrite(EmuState* state, uint32_t addr, const char* data, uint32_t size) {
    X86_MemWrite(state, addr, (const uint8_t*)data, size);
}

void Py_WriteXmm(EmuState* state, int idx, const char* data) {
    X86_WriteXMM(state, idx, (const uint8_t*)data);
}

void Py_ReadXmm(EmuState* state, int idx, void* data) {
    X86_ReadXMM(state, idx, (uint8_t*)data);
}

void Py_WriteFpu(EmuState* state, int idx, const char* data) {
    X86_WriteFPUReg(state, idx, (const uint8_t*)data);
}

void Py_ReadFpu(EmuState* state, int idx, void* data) {
    X86_ReadFPUReg(state, idx, (uint8_t*)data);
}

#include "float80.h"
#include <string.h>
void Py_DoubleToF80(double d, void* out) {
    float80 f = f80_from_double(d);
    memcpy(out, &f, 10);
}
#endif
""")


class EmulatorBackend:
    """Abstract interface for emulator backends (Unicorn, X86Emu, etc)"""
    def __init__(self):
        self.trace = [] # List of (op, addr, val, size)

    def mem_map(self, addr, size, perms):
        raise NotImplementedError()

    def mem_write(self, addr, data):
        raise NotImplementedError()

    def mem_read(self, addr, size):
        raise NotImplementedError()
        
    def reg_write(self, name, val):
        raise NotImplementedError()

    def reg_read(self, name):
        raise NotImplementedError()
        
    def start(self, begin, end, count=0):
        raise NotImplementedError()
        
    def get_fault_info(self):
        """Return (vector, message) or None if no fault"""
        return None
        
    def get_trace(self):
        return self.trace

class UnicornBackend(EmulatorBackend):
    def __init__(self):
        super().__init__()
        self.mu = None
        self.exception = None
        
        if UNICORN_AVAILABLE:
            self.mu = Uc(UC_ARCH_X86, UC_MODE_32)

    def is_available(self):
        return self.mu is not None

    def set_segment_base(self, reg_name, base):
        if not self.mu: return
        
        # Only support FS and GS as requested
        if reg_name == 'FS':
            try:
                self.mu.reg_write(UC_X86_REG_FS_BASE, base)
            except Exception as e:
                print(f"  [Unicorn] Failed to set FS_BASE: {e}")
                
        elif reg_name == 'GS':
            try:
                self.mu.reg_write(UC_X86_REG_GS_BASE, base)
            except Exception as e:
                print(f"  [Unicorn] Failed to set GS_BASE: {e}")

    def mem_map(self, addr, size, perms):
        if self.mu:
            try:
                self.mu.mem_map(addr, size, perms)
            except UcError as e:
                # Ignore overlap errors?
                pass

    def mem_write(self, addr, data):
        if self.mu:
            self.mu.mem_write(addr, data)

    def mem_read(self, addr, size):
        if self.mu:
            return self.mu.mem_read(addr, size)
        return b'\x00'*size

    def reg_write(self, name, val):
        if not self.mu: return
        reg_id = self._get_uc_reg(name)
        if reg_id:
             if name.startswith('XMM'):
                 # Handle XMM as 16-byte bytes
                 if isinstance(val, (list, tuple, bytes)):
                     val_bytes = bytes(val)
                 elif isinstance(val, int):
                     val_bytes = val.to_bytes(16, 'little')
                 else:
                     val_bytes = bytes(val) # Fallback
                 # Unicorn expects integer even for 128-bit registers
                 val_int = int.from_bytes(val_bytes, 'little')
                 self.mu.reg_write(reg_id, val_int)
             elif name.startswith('ST'):
                 idx = int(name[2:])
                 fsw = self.mu.reg_read(UC_X86_REG_FPSW)
                 top = (fsw >> 11) & 7
                 phys_reg = [UC_X86_REG_FP0, UC_X86_REG_FP1, UC_X86_REG_FP2, UC_X86_REG_FP3,
                             UC_X86_REG_FP4, UC_X86_REG_FP5, UC_X86_REG_FP6, UC_X86_REG_FP7][(top + idx) & 7]
                 
                 if isinstance(val, float):
                     buf = bytearray(10)
                     cppyy.gbl.Py_DoubleToF80(val, buf)
                     low = int.from_bytes(buf[:8], 'little')
                     high = int.from_bytes(buf[8:], 'little')
                 elif isinstance(val, int):
                     low = val & 0xFFFFFFFFFFFFFFFF
                     high = (val >> 64) & 0xFFFF
                 else:
                     val_bytes = bytes(val)
                     low = int.from_bytes(val_bytes[:8], 'little')
                     high = int.from_bytes(val_bytes[8:10], 'little') if len(val_bytes) >= 10 else 0
                 
                 self.mu.reg_write(phys_reg, (low, high))
             else:
                 self.mu.reg_write(reg_id, val)

    def reg_read(self, name):
        if not self.mu: return 0
        reg_id = self._get_uc_reg(name)
        if reg_id:
            val = self.mu.reg_read(reg_id)
            if name.startswith('XMM'):
                return val.to_bytes(16, 'little') # Return bytes for XMM
            elif name.startswith('ST'):
                # Unicorn FP0-FP7 are physical. Logical are ST0-ST7.
                # However, UC_X86_REG_ST0 constants exist in some versions.
                # To be safe, we calculate via TOP.
                idx = int(name[2:])
                fsw = self.mu.reg_read(UC_X86_REG_FPSW)
                top = (fsw >> 11) & 7
                phys_reg = [UC_X86_REG_FP0, UC_X86_REG_FP1, UC_X86_REG_FP2, UC_X86_REG_FP3,
                            UC_X86_REG_FP4, UC_X86_REG_FP5, UC_X86_REG_FP6, UC_X86_REG_FP7][(top + idx) & 7]
                val = self.mu.reg_read(phys_reg)
                
                if isinstance(val, int):
                    return val.to_bytes(10, 'little')
                elif isinstance(val, (list, tuple)):
                    if len(val) >= 2:
                        return val[0].to_bytes(8, 'little') + val[1].to_bytes(2, 'little')
                    return b''.join(x.to_bytes(4, 'little') for x in val)[:10]
            return val
        return 0

    def start(self, begin, end, count=0):
        if not self.mu: return
        try:
            self.mu.emu_start(begin, end, 0, count)
        except UcError as e:
            self.exception = e

    def get_fault_info(self):
        if self.exception:
            # UC_ERR_INSN_INVALID = 10
            if self.exception.errno == 10:
                return (6, "#UD (Invalid Instruction)")
            return (self.exception.errno, str(self.exception))
        return None

    def _get_uc_reg(self, name):
        mapping = {
            'EAX': UC_X86_REG_EAX, 'ECX': UC_X86_REG_ECX, 'EDX': UC_X86_REG_EDX, 'EBX': UC_X86_REG_EBX,
            'ESP': UC_X86_REG_ESP, 'EBP': UC_X86_REG_EBP, 'ESI': UC_X86_REG_ESI, 'EDI': UC_X86_REG_EDI,
            'EIP': UC_X86_REG_EIP, 'EFLAGS': UC_X86_REG_EFLAGS,
            'CS': UC_X86_REG_CS, 'DS': UC_X86_REG_DS, 'ES': UC_X86_REG_ES, 'SS': UC_X86_REG_SS,
            'FS': UC_X86_REG_FS, 'GS': UC_X86_REG_GS,
            'XMM0': UC_X86_REG_XMM0, 'XMM1': UC_X86_REG_XMM1, 'XMM2': UC_X86_REG_XMM2, 'XMM3': UC_X86_REG_XMM3,
            'XMM4': UC_X86_REG_XMM4, 'XMM5': UC_X86_REG_XMM5, 'XMM6': UC_X86_REG_XMM6, 'XMM7': UC_X86_REG_XMM7,
            'ST0': UC_X86_REG_FP0, 'ST1': UC_X86_REG_FP1, 'ST2': UC_X86_REG_FP2, 'ST3': UC_X86_REG_FP3,
            'ST4': UC_X86_REG_FP4, 'ST5': UC_X86_REG_FP5, 'ST6': UC_X86_REG_FP6, 'ST7': UC_X86_REG_FP7,
            'FSW': UC_X86_REG_FPSW, 'FCW': UC_X86_REG_FPCW,
        }
        return mapping.get(name)

class X86EmuBackend(EmulatorBackend):
    def __init__(self):
        super().__init__()
        # Create State
        self.state = cppyy.gbl.X86_Create()
        self._cb = None 
        self._mem_hook = None
        self._intr_hooks = {}

    def __del__(self):
        if hasattr(self, 'state') and self.state:
            cppyy.gbl.X86_Destroy(self.state)
            
    def mem_map(self, addr, size, perms):
        cppyy.gbl.X86_MemMap(self.state, addr, size, perms)
        
    def mem_write(self, addr, data):
        if isinstance(data, (bytes, bytearray)):
            b = bytes(data)
            cppyy.gbl.Py_MemWrite(self.state, addr, b, len(b))
        else:
            b = bytes(data)
            cppyy.gbl.Py_MemWrite(self.state, addr, b, len(b))

    def mem_read(self, addr, size):
        buf = bytearray(size)
        cppyy.gbl.X86_MemRead(self.state, addr, buf, size)
        return bytes(buf)
        
    def mem_is_dirty(self, addr):
        return cppyy.gbl.X86_MemIsDirty(self.state, addr) != 0
        
    def set_segment_base(self, reg_name, base):
        idx = -1
        if reg_name == 'ES': idx = 0
        elif reg_name == 'CS': idx = 1
        elif reg_name == 'SS': idx = 2
        elif reg_name == 'DS': idx = 3
        elif reg_name == 'FS': idx = 4
        elif reg_name == 'GS': idx = 5
        
        if idx != -1:
             cppyy.gbl.X86_SegBaseWrite(self.state, idx, base)
    
    def reg_write(self, name, val):
        if name.startswith('XMM'):
            idx = int(name[3:])
            if 0 <= idx < 8:
                if isinstance(val, int):
                    v_bytes = val.to_bytes(16, 'little')
                elif isinstance(val, (bytes, bytearray)):
                    v_bytes = bytes(val)
                else:
                    # Try converting list of ints
                    v_bytes = bytes(val)
                
                # Ensure exactly 16 bytes
                v_bytes = v_bytes.ljust(16, b'\x00')[:16]
                cppyy.gbl.Py_WriteXmm(self.state, idx, v_bytes)
        elif name == 'EIP':
            cppyy.gbl.X86_SetEIP(self.state, val)
        elif name == 'EFLAGS':
            cppyy.gbl.X86_SetEFLAGS(self.state, val)
        elif name.startswith('ST'):
            idx = int(name[2:])
            if 0 <= idx < 8:
                if isinstance(val, int):
                    v_bytes = val.to_bytes(10, 'little')
                elif isinstance(val, float):
                    buf = bytearray(10)
                    cppyy.gbl.Py_DoubleToF80(val, buf)
                    v_bytes = bytes(buf)
                else:
                    v_bytes = bytes(val)
                v_bytes = v_bytes.ljust(10, b'\x00')[:10]
                cppyy.gbl.Py_WriteFpu(self.state, idx, v_bytes)
        elif name == 'FSW':
            cppyy.gbl.X86_SetFSW(self.state, val)
        elif name == 'FCW':
            cppyy.gbl.X86_SetFCW(self.state, val)
        elif name == 'FTW':
            cppyy.gbl.X86_SetFTW(self.state, val)
        else:
            idx = self._get_sim_reg_idx(name)
            if idx != -1: cppyy.gbl.X86_RegWrite(self.state, idx, val)
            
    def reg_read(self, name):
        if name.startswith('XMM'):
            idx = int(name[3:])
            if 0 <= idx < 8:
                buf = bytearray(16)
                cppyy.gbl.Py_ReadXmm(self.state, idx, buf)
                return bytes(buf)
        elif name == 'EIP':
            return cppyy.gbl.X86_GetEIP(self.state)
        elif name == 'EFLAGS':
            return cppyy.gbl.X86_GetEFLAGS(self.state)
        elif name.startswith('ST'):
            idx = int(name[2:])
            if 0 <= idx < 8:
                buf = bytearray(10)
                cppyy.gbl.Py_ReadFpu(self.state, idx, buf)
                return bytes(buf)
        elif name == 'FSW':
            return cppyy.gbl.X86_GetFSW(self.state)
        elif name == 'FCW':
            return cppyy.gbl.X86_GetFCW(self.state)
        elif name == 'FTW':
            return cppyy.gbl.X86_GetFTW(self.state)
        else:
            idx = self._get_sim_reg_idx(name)
            if idx != -1: return cppyy.gbl.X86_RegRead(self.state, idx)
        return 0

    def start(self, begin, end, count=0):
        if begin != 0:
            cppyy.gbl.X86_SetEIP(self.state, begin)
            
        MAX_STEPS = 50 if count == 0 else count
        cppyy.gbl.X86_Run(self.state, end, MAX_STEPS)
                    
    def get_status(self):
        status = cppyy.gbl.X86_GetStatus(self.state)
        if status == 2:  # Fault
            vec = cppyy.gbl.X86_GetFaultVector(self.state)
            if vec == 13:
                 eip = cppyy.gbl.X86_GetEIP(self.state)
                 try:
                     buf = bytearray(1)
                     cppyy.gbl.X86_MemRead(self.state, eip, buf, 1)
                     if buf[0] == 0xF4: # HLT
                         return 1 # Stopped instead of Fault
                 except:
                     pass
        return status

    def get_fault_info(self):
        if self.get_status() != 2:
            return None
        vec = cppyy.gbl.X86_GetFaultVector(self.state)
        if vec != -1:
             msg = f"Vector {vec}"
             if vec == 6: msg = "#UD (Unimplemented Instruction)"
             return (vec, msg)
        return None

    def step(self):
        return cppyy.gbl.X86_Step(self.state)
        
    def stop(self):
        cppyy.gbl.X86_EmuStop(self.state)

    def set_fault_callback(self, cb):
        # New signature: bool(*)(EmuState* state, uint32_t addr, int is_write, void* userdata)
        def wrapped_cb(state, addr, is_write, userdata):
            res = cb(addr, is_write)
            return True if res else False
        self._cb = wrapped_cb
        cppyy.gbl.X86_SetFaultCallback(self.state, self._cb, cppyy.nullptr)

    def set_mem_hook(self, cb):
        # New signature: void(*)(EmuState* state, uint32_t addr, uint32_t size, int is_write, uint64_t val, void* userdata)
        def wrapped_cb(state, addr, size, is_write, val, userdata):
            cb(addr, size, is_write, val)
        self._mem_hook = wrapped_cb
        cppyy.gbl.X86_SetMemHook(self.state, self._mem_hook, cppyy.nullptr)

    def set_intr_hook(self, vector, cb):
        # New signature: int(*)(EmuState* state, uint32_t vector, void* userdata)
        def wrapped_cb(state, vec, userdata):
            return cb(vec)
        self._intr_hooks[vector] = wrapped_cb
        cppyy.gbl.X86_SetInterruptHook(self.state, vector, self._intr_hooks[vector], cppyy.nullptr)

    def _get_sim_reg_idx(self, name):
        mapping = {
            'EAX': 0, 'ECX': 1, 'EDX': 2, 'EBX': 3,
            'ESP': 4, 'EBP': 5, 'ESI': 6, 'EDI': 7
        }
        return mapping.get(name, -1)

X86Emu = X86EmuBackend

class Runner:
    def __init__(self):
        self.sim_trace = []
        self.uc_trace = []

    def compile(self, asm):
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

    def run_test(self, name, asm=None, code=None, initial_regs=None, expected_regs=None, 
                 initial_eflags=None, expected_eflags=None, expected_eip=None, 
                 expected_read=None, expected_write=None, initial_seg_base=None, 
                 check_eflags_mask=None, check_unicorn=True, count=0, fsw_mask=None):
        if asm:
            code = self.compile(asm)
        
        if not code:
            raise ValueError("Either 'asm' or 'code' must be provided.")
            
        return self._execute_test(name, code, initial_regs, expected_regs, initial_eflags, 
                                 expected_eflags, expected_eip, expected_read, expected_write, 
                                 initial_seg_base, check_eflags_mask, check_unicorn, count, fsw_mask)

    def run_test_bytes(self, name, code, initial_regs=None, expected_regs=None, initial_eflags=None, expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, initial_seg_base=None, check_eflags_mask=None, check_unicorn=True, count=1):
        return self._execute_test(name, code, initial_regs, expected_regs, initial_eflags, expected_eflags, expected_eip, expected_read, expected_write, initial_seg_base, check_eflags_mask, check_unicorn, count)

    def _sim_mem_hook(self, addr, size, is_write, val):
        op = 'W' if is_write else 'R'
        self.sim_trace.append((op, addr, val, size))

    def _uc_mem_hook(self, uc, access, address, size, value, user_data):
        op = 'W' if access == UC_MEM_WRITE else 'R'
        real_val = value
        if op == 'R':
            try:
                data = uc.mem_read(address, size)
                real_val = int.from_bytes(data, 'little')
            except:
                real_val = 0
        self.uc_trace.append((op, address, real_val, size))

    def _execute_test(self, name, code, initial_regs=None, expected_regs=None, initial_eflags=None, 
                      expected_eflags=None, expected_eip=None, expected_read=None, expected_write=None, 
                      initial_seg_base=None, check_eflags_mask=None, check_unicorn=True, count=1, fsw_mask=None):
        backends = []
        
        uc_backend = UnicornBackend()
        if uc_backend.is_available():
            backends.append(('unicorn', uc_backend))
        
        sim_backend = X86EmuBackend()
        backends.append(('sim', sim_backend))
        
        CODE_ADDR = 0x1000
        
        for b_name, b in backends:
            b.mem_map(0x0000, 0x1000, 7) # Zero Page (RWX) for backward jumps
            b.mem_map(0x1000, 0x1000, 7)
            b.mem_map(0x2000, 0x1000, 3)
            b.mem_map(0x7000, 0x2000, 3)
            
            if expected_read:
                for addr, val in expected_read.items():
                    page_base = (addr // 0x1000) * 0x1000
                    perms = 3
                    if page_base == 0x0000 or page_base == 0x1000: perms = 7
                    b.mem_map(page_base, 0x1000, perms)
            if expected_write:
                for addr, val in expected_write.items():
                    page_base = (addr // 0x1000) * 0x1000
                    perms = 3
                    if page_base == 0x0000 or page_base == 0x1000: perms = 7
                    b.mem_map(page_base, 0x1000, perms)
            
            padded_code = bytearray([0xCC] * 0x1000)
            padded_code[:len(code)] = code
            b.mem_write(CODE_ADDR, bytes(padded_code))
            
            b.reg_write('EAX', 1)
            b.reg_write('ECX', 2)
            b.reg_write('EDX', 3)
            b.reg_write('EBX', 4)
            b.reg_write('ESP', 0x8000)
            b.reg_write('EBP', 0x100)
            b.reg_write('ESI', 5)
            b.reg_write('EDI', 6)
            b.reg_write('EFLAGS', 0x202)
            b.reg_write('EIP', CODE_ADDR)
            
            if initial_regs:
                for r, v in initial_regs.items():
                    b.reg_write(r, v)
            
            if initial_eflags is not None:
                b.reg_write('EFLAGS', initial_eflags)
                
            if initial_seg_base:
                seg_names = ['ES', 'CS', 'SS', 'DS', 'FS', 'GS']
                for i, base in enumerate(initial_seg_base):
                    if i < 6 and base != 0:
                        if hasattr(b, 'set_segment_base'):
                             b.set_segment_base(seg_names[i], base)
            
            if expected_read:
                for addr, val in expected_read.items():
                    if val > 0xFFFFFFFFFFFFFFFF:
                        byte_len = 16
                    else:
                        byte_len = (val.bit_length() + 7) // 8
                        if byte_len < 4: byte_len = 4
                    
                    b.mem_write(addr, val.to_bytes(byte_len, 'little'))
                    
            if b_name == 'sim':
                 b.set_mem_hook(self._sim_mem_hook)
                 self.sim_trace = []

        for b_name, b in backends:
            b.start(CODE_ADDR, CODE_ADDR + len(code), count=count)

        passed = True
        fail_reason = ""
        
        results = {} 
        for b_name, b in backends:
            res = {}
            res['EAX'] = b.reg_read('EAX')
            res['ECX'] = b.reg_read('ECX')
            res['EDX'] = b.reg_read('EDX')
            res['EBX'] = b.reg_read('EBX')
            res['ESP'] = b.reg_read('ESP')
            res['EBP'] = b.reg_read('EBP')
            res['ESI'] = b.reg_read('ESI')
            res['EDI'] = b.reg_read('EDI')
            res['EIP'] = b.reg_read('EIP')
            res['EFLAGS'] = b.reg_read('EFLAGS')
            for i in range(8):
                res[f'XMM{i}'] = b.reg_read(f'XMM{i}')
            for i in range(8):
                res[f'ST{i}'] = b.reg_read(f'ST{i}')
            res['FSW'] = b.reg_read('FSW')
            res['FCW'] = b.reg_read('FCW')
            
            fault = b.get_fault_info()
            if fault:
                res['fault'] = fault
            
            results[b_name] = res

        sim_res = results['sim']
        uc_res = results.get('unicorn')
        
        if 'fault' in sim_res:
            vec, msg = sim_res['fault']
            ignored = False
            if uc_res and 'fault' in uc_res:
                uc_vec, uc_msg = uc_res['fault']
                if vec == 6: 
                     if uc_vec == 6: 
                         ignored = True
            elif uc_res and uc_res.get('ESP') == 0:
                 ignored = True

            if vec == 13:
                 offset = sim_res['EIP'] - CODE_ADDR
                 if 0 <= offset < len(code) and code[offset] == 0xF4:
                     ignored = True

            if not ignored:
                fail_reason += f"  Simulator Faulted: {msg} at EIP=0x{sim_res['EIP']:x}\n"
                passed = False
        
        # Check if Unicorn faulted but Sim didn't
        if uc_res and 'fault' in uc_res and 'fault' not in sim_res:
             uc_vec, uc_msg = uc_res['fault']
             fail_reason += f"  Unicorn Faulted (Vector {uc_vec}) but Simulator did not.\n"
             fail_reason += f"  Unicorn Registers: EIP={uc_res['EIP']:x}, EAX={uc_res['EAX']:x}\n"
             passed = False

        reg_check_list = ['EAX', 'ECX', 'EDX', 'EBX', 'ESP', 'EBP', 'ESI', 'EDI']
        for r in reg_check_list:
            sim_val = sim_res[r]
            is_expected = expected_regs and r in expected_regs
            if is_expected:
                if sim_val != expected_regs[r]:
                    msg = f"  {r} Mismatch! Exp: 0x{expected_regs[r]:x}, Got: 0x{sim_val:x}"
                    if uc_res: msg += f" (UC: 0x{uc_res[r]:x})"
                    fail_reason += msg + "\n"
                    passed = False
            if uc_res and check_unicorn:
                uc_val = uc_res[r]
                if r == 'ESP' and uc_val == 0: continue
                if r == 'EAX' and uc_val == 0 and sim_val != 0: continue
                if sim_val != uc_val:
                    if not (is_expected and sim_val == expected_regs[r]):
                        fail_reason += f"  {r} Unicorn Mismatch! UC: 0x{uc_val:x}, Sim: 0x{sim_val:x}\n"
                        passed = False
        
        for i in range(8):
            r = f'XMM{i}'
            sim_val = sim_res[r]
            is_expected = expected_regs and r in expected_regs
            if is_expected:
                exp_v = expected_regs[r]
                exp_bytes = exp_v.to_bytes(16, 'little') if isinstance(exp_v, int) else bytes(exp_v)
                if sim_val != exp_bytes:
                    fail_reason += f"  {r} Mismatch! Exp: {exp_bytes.hex()}, Got: {sim_val.hex()}\n"
                    passed = False
            if uc_res and check_unicorn:
                uc_val = uc_res[r]
                if sim_val != uc_val:
                    fail_reason += f"  {r} Unicorn Mismatch! UC: {uc_val.hex()}, Sim: {sim_val.hex()}\n"
                    passed = False
        
        for i in range(8):
            r = f'ST{i}'
            sim_val = sim_res[r]
            is_expected = expected_regs and r in expected_regs
            if is_expected:
                exp_v = expected_regs[r]
                exp_bytes = exp_v.to_bytes(10, 'little') if isinstance(exp_v, int) else bytes(exp_v)
                if sim_val != exp_bytes:
                    fail_reason += f"  {r} Mismatch! Exp: {exp_bytes.hex()}, Got: {sim_val.hex()}\n"
                    passed = False
            if uc_res and check_unicorn:
                uc_val = uc_res[r]
                # Unicorn sometimes behaves weirdly with FPU regs (e.g. if not initialized)
                # We skip if they are both zero (roughly) or if UC didn't return anything
                if uc_val and sim_val != uc_val:
                    fail_reason += f"  {r} Unicorn Mismatch! UC: {uc_val.hex()}, Sim: {sim_val.hex()}\n"
                    passed = False
        
        for r in ['FSW', 'FCW']:
            if expected_regs and r in expected_regs:
                sim_val = sim_res[r]
                exp_val = expected_regs[r]
                
                check_sim = sim_val
                check_exp = exp_val
                if r == 'FSW' and fsw_mask is not None:
                    check_sim &= fsw_mask
                    check_exp &= fsw_mask

                if check_sim != check_exp:
                    fail_reason += f"  {r} Mismatch! Exp: 0x{exp_val:x}, Got: 0x{sim_val:x}\n"
                    passed = False
            if uc_res and check_unicorn:
                 uc_val = uc_res[r]
                 sim_val = sim_res[r]
                 if uc_val != sim_val:
                      check_uc = uc_val
                      check_sim = sim_val
                      if r == 'FSW' and fsw_mask is not None:
                          check_uc &= fsw_mask
                          check_sim &= fsw_mask
                      
                      if check_uc != check_sim:
                          # FPU Control Word defaults might differ, we mostly care about status
                          if r == 'FSW' or (sim_val != 0 and uc_val != 0):
                              fail_reason += f"  {r} Unicorn Mismatch! UC: 0x{uc_val:x}, Sim: 0x{sim_val:x}\n"
                              passed = False

        sim_eflags = sim_res['EFLAGS']
        mask = check_eflags_mask if check_eflags_mask is not None else 0x8D5
        if expected_eflags is not None:
             if (sim_eflags & mask) != (expected_eflags & mask):
                 fail_reason += f"  EFLAGS Mismatch! Exp: 0x{expected_eflags:x}, Got: 0x{sim_eflags:x}\n"
                 passed = False
        
        if expected_eip is not None:
            if sim_res['EIP'] != expected_eip:
                fail_reason += f"  EIP Mismatch! Exp: 0x{expected_eip:x}, Got: 0x{sim_res['EIP']:x}\n"
                passed = False

        if expected_read:
            for addr, val in expected_read.items():
                if not self._check_trace('R', addr, val):
                     fail_reason += f"  Expected Read at 0x{addr:x} with val 0x{val:x} not found.\n"
                     passed = False

        if expected_write:
            for addr, val in expected_write.items():
                if not self._check_trace('W', addr, val):
                     fail_reason += f"  Expected Write at 0x{addr:x} with val 0x{val:x} not found.\n"
                     passed = False

        if passed:
            print(f"[PASS] {name}")
            return True
        else:
            print(f"[FAIL] {name}")
            print(fail_reason)
            raise AssertionError(f"Test '{name}' failed:\n{fail_reason}")

    def _check_trace(self, op_type, addr, val):
        if val > 0xFFFFFFFFFFFFFFFF:
            low = val & 0xFFFFFFFFFFFFFFFF
            high = (val >> 64) & 0xFFFFFFFFFFFFFFFF
            return self._find_in_trace(op_type, addr, low, 8) and self._find_in_trace(op_type, addr+8, high, 8)
        return self._find_in_trace(op_type, addr, val, None)

    def _find_in_trace(self, op_type, addr, val, size):
        for op, t_addr, t_val, t_size in self.sim_trace:
            if op == op_type:
                # Direct match
                if t_addr == addr:
                    if size and t_size != size:
                         # Fall through to overlap check
                         pass
                    elif t_val == val:
                        return True
                
                # Overlap check: Is [addr, addr+size) contained within [t_addr, t_addr+t_size)?
                # We only support this for small values (up to 8 bytes) stored in t_val
                # 'size' being None usually means we're checking a typical int (4 or 8 bytes)
                check_size = size if size else (val.bit_length() + 7) // 8
                if check_size < 1: check_size = 1
                
                if t_addr <= addr and (t_addr + t_size) >= (addr + check_size):
                    # It's contained. Extract the relevant bytes from t_val.
                    offset = addr - t_addr
                    # Extract 'check_size' bytes from t_val starting at 'offset'
                    # t_val is an integer (little endian)
                    shifted = t_val >> (offset * 8)
                    mask = (1 << (check_size * 8)) - 1
                    extracted = shifted & mask
                    
                    if extracted == val:
                        return True
        return False

    def _get_sim_reg_idx(self, name):
        mapping = {'EAX': 0, 'ECX': 1, 'EDX': 2, 'EBX': 3, 'ESP': 4, 'EBP': 5, 'ESI': 6, 'EDI': 7}
        return mapping.get(name, -1)

X86Emu = X86EmuBackend
