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
3.  **Caching:** This `DecodedBlock` is cached in a `HashMap<eip, DecodedBlock*>`.
4.  **Dispatch:**
    *   The interpreter loop iterates over the `DecodedBlock` array.
    *   **Tail Calls:** Uses `[[clang::musttail]]` to jump from one opcode handler to the next, derived from the `handler_index`.
    *   **Calling Convention:** All handlers use `__attribute__((preserve_none))` to minimize register spilling, as they act largely like jumps.

### 3.2 Opcode Handlers
Each opcode is implemented as a standalone `force_inline` function (or a template for variants).
-   **Wrapper:** A thin wrapper function (the actual target of the function pointer) calls the inline implementation and then tail-calls the next handler.

```cpp
// Conceptual signature
using OpHandler = void(*)(Context* ctx, const DecodedOp* op);

[[clang::musttail]] __attribute__((preserve_none))
void ExecuteAddRegMem(Context* ctx, const DecodedOp* op) {
    impl_add_reg_mem(ctx, op); // force_inline
    // Next instruction dispatch
    auto next_op = op + 1;
    OpHandler next_handler = GlobalHandlers[next_op->handler_idx];
    return next_handler(ctx, next_op);
}
```

### 3.3 Memory Management (SoftMMU)
-   **Flat Memory Model:** 4GB logic address space.
-   **Implementation:** A simple 2-level page table or mask-based lookup.
    -   `HostAddr = PageTable[GuestAddr >> 12] + (GuestAddr & 0xFFF)`
-   **Safety:** Simple bounds checking at the page level.

### 3.4 CPU State (`Context`)
Aligned with hardware layout for SSE compatibility.
```cpp
struct alignas(64) Context {
    uint32_t regs[8];    // EAX, ECX, EDX, EBX, ESP, EBP, ESI, EDI
    uint32_t eip;
    uint32_t eflags;
    alignas(16) __m128 xmm[8];
    uint32_t mxcsr;
    
    // Segment State
    // User-mode simulation implies "Flat Model" (CS=DS=SS=ES=0 base).
    // However, FS and GS are used for TLS and can have non-zero bases.
    // These bases are set by the Host/Caller, not by guest 'mov gs, ax' (mostly).
    uint32_t seg_base[6]; // CS, DS, SS, ES, FS, GS (Indexed 0-5)
};

// API Note: Caller sets ctx.seg_base[SEG_FS] / [SEG_GS] for TLS.
// Guest instructions like 'mov gs, ax' may be ignored or strictly validated 
// to only allow specific values if needed, but 'GS: [0]' will use seg_base[GS].

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
│   ├── common.h        # Types, Context definition
│   ├── decoder.cpp     # x86 -> IR translator
│   ├── dispatch.cpp    # Main loop & Tables
│   ├── mmu.h           # Memory Map
│   └── ops/            # Instruction implementations
│       ├── alu.h
│       ├── mov.h
│       └── sse.h
└── tests/
    └── framework.cpp   # Unicorn comparison runner
```
