# x86 IA-32 Simulator Design Specification

## 1. Project Overview
This project aims to build a high-performance, non-JIT, user-mode x86 IA-32 simulator. The primary goal is execution speed using a **Threaded Interpreter** model with **Pre-decoded Basic Block Caching**.

## 2. Technical Stack & Constraints
-   **Language:** **C++23**.
-   **Compiler:** `clang++` (Targeting macOS/Linux).
-   **Build System:** CMake.
-   **Dependencies:** **Zero external dependencies** for the simulator runtime (C++ Standard Library only).
-   **Analysis Tools:** Python + Zydis + SQLite (Offline instruction analysis).

## 3. Architecture

### 3.1 Execution Model: Cached Threaded Interpreter
The execution engine transforms raw x86 machine code into an internal "Decoded Block" format on the fly (Just-In-Time Decoding, but not JIT Compilation).

**Mechanism:**
1.  **Block Translation:** When the `eip` points to a new address, the decoder reads x86 instructions until a basic block boundary (jump, call, ret, or block size limit).
2.  **Internal Representation (IR):** The block is converted into a `DecodedBlock` array. Each entry contains:
    *   `handler_index`: Index into the global function pointer array.
    *   `metadata`: A bitset/struct containing prefixes, addressing modes, and immediate values (if small/embedded) or offsets.
3.  **Caching:** This `DecodedBlock` is cached in a `ankerl::unordered_dense::map<uint32_t, BasicBlock>`.
4.  **Dispatch:**
    *   The interpreter loop iterates over the `DecodedBlock` array.
    *   **Tail Calls:** Uses `[[clang::musttail]]` to jump from one opcode handler to the next, derived from the `handler_index`.
    *   **Calling Convention:** All handlers use `__attribute__((preserve_none))` to minimize register spilling, as they act largely like jumps.

### 3.2 Opcode Handlers
Each opcode is implemented as a standalone `force_inline` function (or a template for variants).
-   **Wrapper:** A thin wrapper function (the actual target of the function pointer) calls the inline implementation and then tail-calls the next handler.

```cpp
// Conceptual signature
using HandlerFunc = void(ATTR_PRESERVE_NONE *)(EmuState* state, DecodedOp* op);

[[clang::musttail]] ATTR_PRESERVE_NONE
void ExecuteAddRegMem(EmuState* state, DecodedOp* op) {
    impl_add_reg_mem(state, op); // force_inline
    // Next instruction dispatch
    auto next_op = op + 1;
    HandlerFunc next_handler = g_Handlers[next_op->handler_index];
    return next_handler(state, next_op);
}
```

### 3.3 Memory Management (SoftMMU)
-   **Flat Memory Model:** 4GB logic address space.
-   **Implementation:** `SoftMMU` class with a 2-level page table.
    -   Level 1: Directory (1024 entries).
    -   Level 2: Table (1024 entries of 4KB pages).
-   **Features:**
    -   **Permissions:** Read/Write/Exec tracking (placeholder).
    -   **Hooks:** `MemHook` callback for tracing memory accesses.
    -   **Faults:** `FaultHandler` callback for handling invalid accesses (e.g., segfaults).
-   **Safety:** Bounds checking and page-level valid bit.

### 3.4 CPU State (`Context`)
Aligned with hardware layout for SSE properties and expanded for FPU/State.
```cpp
struct alignas(64) Context {
    uint32_t regs[8];    // EAX, ECX, EDX, EBX, ESP, EBP, ESI, EDI
    uint32_t eip;
    uint32_t eflags;
    uint32_t eflags_mask; // Mask for user-modifiable flags

    // SSE/SSE2 Registers
    alignas(16) simde__m128 xmm[8];
    uint32_t mxcsr;
    
    // FPU Registers (80-bit Extended Precision)
    alignas(16) float80 fpu_regs[8];
    uint16_t fpu_sw;
    uint16_t fpu_cw;
    uint16_t fpu_tw;
    int fpu_top;

    // Segment Base Addresses (Flat Model + TLS)
    uint32_t seg_base[6]; // CS, DS, SS, ES, FS, GS

    // System Environment
    void* mmu;   // Pointer to SoftMMU
    void* hooks; // Pointer to HookManager
};
```

### 3.5 FPU Support (x87)
-   **Data Types:** `float80` (Standard 80-bit extended precision) implemented via `long double` or struct wrapper.
-   **Stack:** 8-slot register stack with `TOP` pointer.
-   **Status:** Full x87 status word (SW) and control word (CW) emulation.

### 3.6 EmuState
Encapsulates the entire simulator state:
```cpp
struct EmuState {
    Context ctx;
    SoftMMU mmu;
    HookManager hooks;
    EmuStatus status;
    ankerl::unordered_dense::map<uint32_t, BasicBlock> block_cache;
};
```

## 4. Development Workflow

### Phase 1: Instruction Collection (Offline)
1.  **Compile Redis:** Use `zig cc -target i686-linux-gnu` to cross-compile Redis to 32-bit object files.
2.  **Analyze (`analyze/`):**
    -   Python script uses `zydis` to decode all `.o` files.
    -   Store unique `(Mnemonic, OperandTypes, Encoding)` tuples in SQLite.
    -   Output: A manifest of *actually used* instructions to prioritize implementation.

### Phase 2: Core Engine (C++23)
1.  **Setup:** CMake + Clang++.
2.  **Decoder:** Implement the raw byte -> `DecodedOp` translator.
3.  **Dispatcher:** Implement the `musttail` loop and `preserve_none` definitions.
4.  **MMU:** Basic read/write functions.

### Phase 3: Opcode Implementation (LLM Assisted)
For each prioritized instruction format:
1.  **Generate Test:** LLM writes a snippet (e.g., `add eax, [ebx+4]`).
2.  **Verify w/ Unicorn:** Run snippet in Unicorn, save `Context` result.
3.  **Implement Handler:** Write the C++23 handler.
4.  **Verify:** Run in Simulator, compare `Context`.

## 5. Directory Structure
```
x86emu/
├── CMakeLists.txt
├── analyze/            # Python/Zydis analysis
├── src/
│   ├── main.cpp
│   ├── common.h        # Types, Context, SIMDe
│   ├── decoder.cpp     # x86 -> IR translator
│   ├── decoder.h       # DecodedOp, BasicBlock
│   ├── decoder_lut.h   # Lookup tables
│   ├── dispatch.h      # Dispatcher loop
│   ├── mmu.h           # SoftMMU implementation
│   ├── state.h         # EmuState definition
│   ├── float80.cpp     # 80-bit floating point class
│   ├── bindings.cpp    # C API for Python integration
│   └── ops/            # Instruction implementations
│       ├── ops_alu.cpp
│       ├── ops_fpu.cpp
│       ├── ops_sse_fp.cpp
│       ├── ops_sse_int.cpp
│       └── ...
└── tests/
    ├── framework.cpp     # Unicorn comparison runner
    └── regression/       # Python regression tests

```
