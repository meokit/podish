import re

def preprocess_header(content):
    # Remove #includes
    content = re.sub(r'#include.*', '', content)
    # Remove #pragma once
    content = re.sub(r'#pragma once', '', content)
    
    # Remove extern "C" block
    content = re.sub(r'extern "C"\s*\{', '', content)
    # The closing brace for extern "C" is hard to distinguish from others. 
    # But usually it's at end of file.
    
    # Remove namespace x86emu {
    content = re.sub(r'namespace x86emu\s*\{', '', content)
    
    # Remove closing braces at end of file (hacky but works if file ends with namespace/extern closure)
    # content = re.sub(r'\}\s*// namespace x86emu', '', content) # Comment stripped already
    # Maybe just rely on cffi ignoring extra braces? No, it won't.
    # Let's count braces or just remove lines that only have "}" ?
    
    # Remove extern __thread
    content = re.sub(r'extern __thread.*?;', '', content)

    # Convert alignas(N) to __attribute__((aligned(N)))
    # Match various forms of alignas
    content = re.sub(r'alignas\s*\(([^)]+)\)', r'__attribute__((aligned(\1)))', content)
    
    # Remove constexpr
    content = re.sub(r'constexpr', 'const', content)
    
    # Remove default initializers
    content = re.sub(r'\s*=\s*[^;,}]+(?=;)', '', content)
    
    # Replace enum class with enum
    content = content.replace('enum class', 'enum')
    
    # Handle __m128
    # Replace with aligned struct
    content = re.sub(r'__m128', 'xmm_reg_t', content)
    
    # Handle SoftMMU, HookManager, unordered_map (replace with void*)
    # These are in EmuState.
    # struct EmuState { Context ctx; SoftMMU mmu; ... }
    # SoftMMU is C++ class. cffi can't parse it.
    # We should Replace "SoftMMU mmu;" with "char mmu[...];" ? No size unknown.
    # OR replace EmuState definition with just "struct EmuState { Context ctx; ... };" 
    # but we need size to match if we allocate it in C++?
    # Wait, X86_Create allocates EmuState in C++. Python receives a pointer.
    # Python accesses state->ctx.
    # So Python only needs to know the layout of 'struct EmuState' UP TO 'ctx'.
    # If 'ctx' is the first member, we can cast EmuState* to Context* ?
    # EmuState definition:
    # struct EmuState { Context ctx; SoftMMU mmu; ... }
    # Yes, ctx is first.
    # So we can define "struct EmuState { Context ctx; };" in cdef.
    # cffi handles pointers fine.
    
    # Remove std::
    content = content.replace("std::", "")
    
    # Remove private/public
    content = re.sub(r'public:', '', content)
    content = re.sub(r'private:', '', content)
    
    # Remove template stuff? common.h doesn't seem to have templates.
    
    lines = content.splitlines()
    cleaned_lines = []
    brace_balance = 0
    for line in lines:
        line = re.sub(r'//.*', '', line)
        if line.strip().startswith('#'): continue
        if "unordered_map" in line: continue # Skip C++ fields
        if "SoftMMU" in line: continue
        if "HookManager" in line: continue
        if "BasicBlock" in line: continue
        if line.strip() == "}": continue # Remove standalone closing braces (from namespace)
        
        cleaned_lines.append(line)
        
    content = "\n".join(cleaned_lines)
    
    return content

def get_cdef():
    cdef = """
    typedef struct { char _x[16]; } xmm_reg_t __attribute__((aligned(16)));
    """
    
    # We need to correctly order or concat them.
    # float80.h is included by common.h? No, other way around or independent.
    # common.h needs float80?
    
    # Let's read float80.h first as common.h likely uses it.
    with open("src/float80.h", "r") as f:
        cdef += preprocess_header(f.read())
        
    with open("src/common.h", "r") as f:
        cdef += preprocess_header(f.read())
        
    # We also need EmuState definition from state.h? 
    # Or common.h only has Context.
    # X86_Create returns EmuState*. EmuState is an opaque pointer in bindings.cpp mostly?
    # bindings.cpp: EmuState* state.
    # But python needs to access Context.
    # bindings.cpp returns "void*" or "EmuState*".
    # User accesses structure via offset in run_command output analysis?
    
    # Wait, runner.py currently accesses "Context".
    # The EmuState struct is in src/state.h.
    # Does the user access fields of EmuState or just Context?
    # runner.py: self.ctx = self.emu.ctx. 
    # But self.emu was cast to POINTER(EmuState)?
    # runner.py: "class EmuState(ctypes.Structure): _fields_ = [('ctx', Context), ...]"
    
    # So we need state.h too.
    # Struct EmuState (Manual definition for CFFI, C++ members omitted)
    cdef += """
    struct EmuState {
        Context ctx;
        // Opaque padding for C++ objects if needed, but since we access via pointer and ctx is first, 
        // we can just pretend it ends here or has dummy tail.
        // But for safety, let's just expose ctx.
    };
    """
         
    # We need bindings API function signatures.
    # bindings.cpp has extern "C".
    # We can just write them manually or extract.
    # Manual is safer for now.
    
    cdef += """
    void* X86_Create();
    void X86_Destroy(void* state);
    void X86_SetFaultCallback(void* state, void* cb, void* opaque);
    void X86_SetMemHook(void* state, void* cb, void* opaque);
    int X86_Decode(void* state, uint32_t addr, void* out_op);
    void X86_MemMap(void* state, uint32_t addr, uint32_t size, uint8_t perms);
    void X86_MemWrite(void* state, uint32_t addr, uint64_t val, int size);
    uint64_t X86_MemRead(void* state, uint32_t addr, int size);
    void X86_RunInstructions(void* state, uint32_t addr, int count);
    int X86_Step(void* state);
    """
    
    return cdef

if __name__ == "__main__":
    print(get_cdef())
