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

TOP_50_INSTRUCTIONS = """
mov r32, m32 : 44558
push r32 : 34520
call imm32 : 28866
mov m32, r32 : 26315
add r32, imm32 : 22167
mov r32, r32 : 14795
pop r32 : 14650
sub r32, imm32 : 14584
lea r32, m32 : 14260
jmp imm32 : 12316
je imm32 : 11899
push m32 : 10278
test r32, r32 : 9938
jne imm32 : 7270
push imm32 : 7104
mov m32, imm32 : 6726
cmp r32, imm32 : 5203
xor r32, r32 : 4982
mov r32, imm32 : 4153
cmp m32, imm32 : 4016
ret  : 3552
nop m16 : 3120
nop m32 : 2993
add r32, r32 : 2526
movzx r32, m8 : 2512
and r32, imm32 : 2203
movsd m64, r128 : 2168
cmp r32, r32 : 1808
nop  : 1545
shr r32, imm8 : 1543
movsd r128, m64 : 1501
inc r32 : 1475
ja imm32 : 1225
cmp r32, m32 : 1153
movzx r32, m16 : 964
test r8, imm8 : 945
jmp r32 : 880
or r32, r32 : 848
jg imm32 : 806
sete r8 : 783
mov m8, r8 : 724
jb imm32 : 718
sub r32, r32 : 712
test m8, imm8 : 710
add r32, m32 : 675
cmp m8, imm8 : 661
mov m8, imm8 : 654
setne r8 : 654
jle imm32 : 650
cmove r32, r32 : 625
"""

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
        self.lib.X86_EmuStop.argtypes = [ctypes.c_void_p] # Add binding
        self.lib.X86_Destroy.argtypes = [ctypes.c_void_p]
        
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

    def stop(self):
        self.lib.X86_EmuStop(self.state)

    def set_fault_callback(self, cb):
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
        except FileNotFoundError:
             raise Exception("NASM not found. Please install nasm.")
        except subprocess.CalledProcessError as e:
             raise Exception(f"NASM Conversion Failed: {e}")
        finally:
            if os.path.exists(asm_path): os.remove(asm_path)
            if os.path.exists(bin_path): os.remove(bin_path)

    def run_test(self, name, asm_code, initial_regs=None, expected_regs=None, expected_eip=None):
        print(f"\n[*] Running Test: {name}")
        try:
            code = self.compile(asm_code)
            print(f"  [.] Compiled {len(code)} bytes: {code.hex()}")
        except Exception as e:
            print(f"  [-] Compilation Failed: {e}")
            return False
            
        ADDRESS = 0x1000
        STACK_ADDR = 0x2000
        STACK_SIZE = 0x1000
        STACK_TOP = STACK_ADDR + 0x800 # Arbitrary top

        # Uniocorn Setup (Truth Machine)
        uc_state = {}
        if UNICORN_AVAILABLE:
            try:
                uc = Uc(UC_ARCH_X86, UC_MODE_32)
                uc.mem_map(ADDRESS, 0x1000)
                uc.mem_write(ADDRESS, code)
                uc.mem_map(STACK_ADDR, STACK_SIZE)
                
                # Init Regs
                uc.reg_write(UC_X86_REG_ESP, STACK_TOP)
                if initial_regs:
                    uc_map = {
                        0: UC_X86_REG_EAX, 1: UC_X86_REG_ECX, 2: UC_X86_REG_EDX, 3: UC_X86_REG_EBX,
                        4: UC_X86_REG_ESP, 5: UC_X86_REG_EBP, 6: UC_X86_REG_ESI, 7: UC_X86_REG_EDI
                    }
                    for idx, val in initial_regs.items():
                        if idx in uc_map:
                            uc.reg_write(uc_map[idx], val)

                # Hook HLT (Instr) to Stop
                def hook_code(uc, address, size, user_data):
                    # Check for HLT (0xF4)
                    mem = uc.mem_read(address, 1)
                    if mem[0] == 0xF4:
                        uc.emu_stop()
                        
                uc.hook_add(UC_HOOK_CODE, hook_code)
                
                print("  [.] Executing (Unicorn)...")
                uc.emu_start(ADDRESS, ADDRESS + len(code) + 100, timeout=100000) # +100 for safety? HLT stops it.
                
                # Capture State
                uc_map_rev = {
                    0: UC_X86_REG_EAX, 1: UC_X86_REG_ECX, 2: UC_X86_REG_EDX, 3: UC_X86_REG_EBX,
                    4: UC_X86_REG_ESP, 5: UC_X86_REG_EBP, 6: UC_X86_REG_ESI, 7: UC_X86_REG_EDI
                }
                for i in range(8):
                    uc_state[i] = uc.reg_read(uc_map_rev[i])
                uc_state['eip'] = uc.reg_read(UC_X86_REG_EIP)
                
            except UcError as e:
                print(f"  [-] Unicorn Error: {e}")
                # We can continue to test Simulator even if Unicorn fails, but mark it.
        
        # Setup Simulator
        try:
            sim = X86Emu()
            
            # Fault Handler (calling stop)
            def on_fault(addr, is_write):
                print(f"[Callback] Fault at 0x{addr:X} (Write={is_write})")
                sim.stop()
                
            sim.set_fault_callback(on_fault)
            
            # Memory Map
            sim.mem_map(ADDRESS, 0x1000, 7) # RWX
            sim.mem_write(ADDRESS, code)
            sim.mem_map(STACK_ADDR, STACK_SIZE, 7) # RWX Stack
            
            # Init Registers
            # Default Stack
            sim.ctx.regs[4] = STACK_TOP
            
            if initial_regs:
                for idx, val in initial_regs.items():
                    sim.ctx.regs[idx] = val
                    
            sim.ctx.eip = ADDRESS
            
            # Run
            print("  [.] Executing (X86_Run)...")
            sim.run()
            
            # Verification
            success = True
            
            # Verify vs Expected or Unicorn
            final_eip = sim.ctx.eip
            current_regs = list(sim.ctx.regs)

            # 1. Check against Unicorn (Truth Machine)
            if UNICORN_AVAILABLE and uc_state:
                # Compare Regs
                for i in range(8):
                    if current_regs[i] != uc_state[i]:
                        print(f"  [-] Unicorn Mismatch Reg {i}: Sim=0x{current_regs[i]:X} vs Uc=0x{uc_state[i]:X}")
                        success = False
                    # else:
                    #     print(f"  [+] Reg {i} Matches Unicorn")
                
                # Compare EIP - (Note: HLT handling might differ slightly in final EIP?)
                # If Unicorn stopped AT HLT, EIP points TO HLT.
                # If Simulator stopped AFTER HLT, EIP points AFTER HLT?
                # My OpHlt usually sets status=Stopped. DispatchWrapper adds length.
                # So Sim EIP likely points to Next Instruction.
                # Unicorn: Hook stops BEFORE execution? Or After?
                # Check logic.
                pass

            # 2. Check Explicit Expectations (Backwards Compatibility)
            if expected_eip:
                if final_eip != expected_eip:
                    # Allow slight divergence if Unicorn matches?
                    # For now keep explicit check
                    print(f"  [-] EIP Mismatch: 0x{final_eip:X} (Expected 0x{expected_eip:X})")
                    success = False
                else:
                    print(f"  [+] EIP Correct: 0x{final_eip:X}")
            
            # Verify Registers (Explicit)
            if expected_regs:
                for idx, val in expected_regs.items():
                    if current_regs[idx] != val:
                         print(f"  [-] Register {idx} Mismatch: 0x{current_regs[idx]:X} (Expected 0x{val:X})")
                         success = False
                    else:
                         print(f"  [+] Register {idx} Correct: 0x{val:X}")
                         
            if success:
                print(f"  [+] SUCCESS: {name}")
                return True
            else:
                print(f"  [-] FAILURE: {name}")
                return False

        except Exception as e:
            print(f"  [-] Exception during test: {e}")
            return False

def test_stack_lea():
    runner = TestRunner()
    asm = """
    lea eax, [ebx + 4]
    push eax
    push 0x12345678
    pop ecx
    pop edx
    hlt
    """
    
    # Init: EBX = 0x20
    regs_init = {3: 0x20} # EBX
    
    # Expected: 
    # EAX = 0x24 (36)
    # ECX = 0x12345678
    # EDX = 0x24
    # ESP = Initial (0x2800) because push/push/pop/pop balance
    regs_expected = {
        0: 0x24,        # EAX
        1: 0x12345678,  # ECX
        2: 0x24,        # EDX
        4: 0x2800       # ESP
    }
    
    # Expected EIP: 1000 + length of code
    # LEA(3) + PUSH(1) + PUSH_IMM(5) + POP(1) + POP(1) + HLT(1) = 12 (0xC)
    # Start 0x1000 -> End 0x100C
    runner.run_test("Stack & LEA", asm, initial_regs=regs_init, expected_regs=regs_expected, expected_eip=0x100C)

def test_add():
    runner = TestRunner()
    # Rank 5: add r32, imm32 (05 / 81 / 83)
    # Rank 24: add r32, r32
    asm = """
    mov eax, 0x10
    add eax, 0x20       ; ADD EAX, Imm32 (or Imm8 sign ext)
    mov ebx, 0x5
    add eax, ebx        ; ADD EAX, EBX
    hlt
    """
    
    # Expected:
    # 1. MOV EAX, 0x10 -> 16
    # 2. ADD EAX, 0x20 -> 0x30 (48)
    # 3. MOV EBX, 0x5
    # 4. ADD EAX, EBX  -> 0x35 (53)
    
    regs_expected = {
        0: 0x35, # EAX
        3: 0x5   # EBX
    }
    
    # EIP:
    # mov eax, imm (B8 10 00 00 00) = 5
    # add eax, imm (83 C0 20) = 3 (if optimized) or (05 20 00 00 00) = 5
    # NASM usually optimizes `add eax, 0x20` to `83 C0 20` (add r/m32, imm8).
    # mov ebx, 5 (BB 05 00 00 00) = 5
    # add eax, ebx (01 D8) = 2
    # hlt = 1
    # Total could vary. We rely on logic verification mostly.
    
    runner.run_test("ADD (r32, imm32 / r32, r32)", asm, expected_regs=regs_expected)

def test_sub_cmp_jcc():
    runner = TestRunner()
    # Rank 8: sub r32, imm32
    # Rank 17: cmp r32, imm32
    # Rank 11: je imm32 (0F 84)
    # Rank 10: jmp imm32 (E9)
    
    asm = """
    mov eax, 10
    sub eax, 5      ; EAX = 5
    cmp eax, 5      ; ZF = 1
    je label_equal  ; Should be taken
    mov ebx, 0xBAD  ; Should skip
    hlt
    label_equal:
    mov ebx, 1
    jmp label_end   ; Unconditional jump
    add ebx, 1      ; Should skip
    label_end:
    hlt
    """
    
    regs_expected = {
        0: 5,   # EAX
        3: 1    # EBX
    }
    
    runner.run_test("SUB/CMP/JCC (je, jmp)", asm, expected_regs=regs_expected)

def test_call_ret():
    runner = TestRunner()
    # Rank 3: call imm32
    # Rank 21: ret
    
    asm = """
    mov eax, 10
    call func_double
    add eax, 1
    hlt
    
    func_double:
    add eax, eax
    ret
    """
    
    # Flow:
    # 1. MOV EAX, 10
    # 2. CALL func_double (Push EIP, Jump)
    # 3. ADD EAX, EAX -> 20
    # 4. RET (Pop EIP, Jump back)
    # 5. ADD EAX, 1 -> 21
    # 6. HLT
    
    regs_expected = {
        0: 21
    }
    
    runner.run_test("CALL/RET", asm, expected_regs=regs_expected)

def test_logic():
    runner = TestRunner()
    # Rank 13: test r32, r32
    # Rank 18: xor r32, r32
    # Rank 26: and r32, imm32
    
    asm = """
    mov eax, 0xF0F0F0F0
    xor eax, eax        ; EAX = 0, ZF = 1
    
    mov eax, 0x12345678
    test eax, eax       ; ZF = 0, SF = 0
    jz label_failed     ; Should not jump
    
    mov ebx, 0xFFFF0000
    and ebx, 0x0000FFFF ; EBX = 0, ZF = 1
    jnz label_failed    ; Should not jump
    
    mov ecx, 0x55555555
    or ecx, 0xAAAAAAAA  ; ECX = 0xFFFFFFFF
    
    hlt
    
    label_failed:
    mov eax, 0xBAD      ; Error flag
    hlt
    """
    
    regs_expected = {
        0: 0x12345678, # EAX
        3: 0,          # EBX
        1: 0xFFFFFFFF, # ECX
    }
    
    runner.run_test("LOGIC (XOR, TEST, AND, OR)", asm, expected_regs=regs_expected)

def test_group5():
    runner = TestRunner()
    # Rank 12: push m32 (FF /6)
    # Rank 28: inc r32
    # Rank 32: dec r32
    
    asm = """
    mov esp, 0x1500 ; Initialize Stack (Mid-Page)
    mov eax, 10
    inc eax         ; EAX = 11
    
    mov ebx, 20
    dec ebx         ; EBX = 19
    
    ; Setup memory for PUSH m32
    mov ecx, 0x1000
    mov dword [ecx], 0xDEADBEEF
    
    ; PUSH m32
    push dword [ecx] ; Push 0xDEADBEEF
    
    pop edx         ; EDX = 0xDEADBEEF
    
    hlt
    """
    
    regs_expected = {
        0: 11,          # EAX
        3: 19,          # EBX
        2: 0xDEADBEEF   # EDX
    }
    
    runner.run_test("GROUP5 (INC, DEC, PUSH m32)", asm, expected_regs=regs_expected)

def test_shift_rotate():
    runner = TestRunner()
    # Group 2: Shift/Rotate
    # SHL r32, 1 (D1 /4)
    # SHR r32, 1 (D1 /5)
    # SAR r32, imm8 (C1 /7)
    # ROL r32, cl (D3 /0)
    # ROR r32, 1 (D1 /1)
    
    asm = """
    mov eax, 1
    shl eax, 1      ; EAX = 2 (1 << 1)
    shl eax, 2      ; EAX = 8 (2 << 2) (using C1 /4 imm8) -> Optimized by NASM to C1 E0 02?
    
    mov ebx, 0xFFFFFFFF
    shr ebx, 1      ; EBX = 0x7FFFFFFF
    
    mov ecx, 0xF0F0F0F0
    sar ecx, 4      ; ECX = 0xFF0F0F0F (Arithmetic Shift)
    
    ; Rotate
    mov edx, 0x80000001
    rol edx, 1      ; EDX = 0x00000003 (Rotate Left)
    
    mov esi, 0x00000003
    ror esi, 1      ; ESI = 0x80000001 (Rotate Right)
    
    ; CL Variant
    mov edi, 0x1
    mov ecx, 3      ; CL = 3
    shl edi, cl     ; EDI = 8 (1 << 3)
    
    hlt
    """
    
    regs_expected = {
        0: 8,           # EAX
        3: 0x7FFFFFFF,  # EBX
        1: 0xFF0F0F0F,  # ECX (Overwritten by 3 later?) No.
        # Wait, ECX is used for CL variant `shl edi, cl`.
        # `mov ecx, 3` overwrites ECX.
        # So final ECX should be 3.
        # But before that logic ran, it was 0xFF0F0F0F.
        # We check final state.
        
        2: 0x00000003,  # EDX
        6: 0x80000001,  # ESI
        7: 8,           # EDI
        1: 3            # ECX
    }
    
    runner.run_test("GROUP 2 (SHL, SHR, SAR, ROL, ROR)", asm, expected_regs=regs_expected)

def test_movzx_movsx():
    runner = TestRunner()
    # 0F B6: MOVZX r32, r/m8
    # 0F B7: MOVZX r32, r/m16
    # 0F BE: MOVSX r32, r/m8
    # 0F BF: MOVSX r32, r/m16
    
    asm = """
    mov eax, 0x11223344
    mov bh, 0x80        ; BH = -128 (starts with 1)
    
    ; MOVZX Byte
    movzx eax, bh       ; EAX = 0x00000080 (Zero Extend)
    
    ; MOVSX Byte
    mov ecx, 0
    mov cl, 0x80        ; CL = 0x80 (-128)
    movsx edx, cl       ; EDX = 0xFFFFFF80 (Sign Extend)
    
    ; MOVZX Word
    mov esi, 0x55667788
    mov ax, 0xFFFF
    movzx esi, ax       ; ESI = 0x0000FFFF
    
    ; MOVSX Word
    mov edi, 0
    mov bx, 0x8000      ; BX = -32768
    movsx edi, bx       ; EDI = 0xFFFF8000
    
    hlt
    """
    
    regs_expected = {
        0: 0x0000FFFF,  # EAX (Overwritten by mov ax, 0xFFFF later)
        # Logic:
        # 1. MOV EAX, 0x11223344
        # 2. MOV BH, 0x80. EAX unaffected? BH is in EBX.
        # 3. MOVZX EAX, BH. EAX = 0x00000080.
        # ...
        # ?. MOV AX, 0xFFFF. EAX low 16 becomes FFFF. Top 16 preserved?
        # In 32-bit mode, writes to 16-bit regs merge.
        # So EAX = 0x0000FFFF.
        # Wait. `movzx` overwrites EAX again?
        # No, `movzx` writes EAX.
        # `mov ax, 0xFFFF` writes AX.
        # Let's trace carefully.
        
        # 1. EAX = ...
        # 3. MOVZX EAX, BH -> EAX = 0x80.
        # ...
        # mov ax, 0xFFFF -> EAX = 0x0000FFFF?
        # If EAX was 0x00000080.
        # 0x0000. FFFF.
        # Yes.
    
        2: 0xFFFFFF80,  # EDX (MOVSX)
        6: 0x0000FFFF,  # ESI (MOVZX Word)
        7: 0xFFFF8000,  # EDI (MOVSX Word)
    }
    
    runner.run_test("MOVZX / MOVSX", asm, expected_regs=regs_expected)

if __name__ == "__main__":
    # test_stack_lea()
    # test_add()
    # test_sub_cmp_jcc()
    # test_call_ret()
    # test_logic()
    test_group5()
    test_shift_rotate()
    test_movzx_movsx()


