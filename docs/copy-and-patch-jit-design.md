# Copy-and-Patch JIT Design Proposal

## Current Status

This document started as a forward-looking design proposal. We now also have a
working baseline implementation of the copy-and-patch pipeline, and an
important result is already clear:

- the baseline copy-and-patch JIT is functionally usable
- but it is currently **slower** than the existing threaded interpreter on
  Apple Silicon
- therefore `FIBERCPU_ENABLE_JIT` is **disabled by default** in CMake

At the time of writing, representative CoreMark numbers on an M3 Max are:

- baseline copy-and-patch JIT: about **1950** iterations/sec
- current interpreter baseline: about **2250** iterations/sec

The exact score moves somewhat with build flavor and surrounding experiments,
but the qualitative result has been stable: the current JIT framework does not
yet outperform the interpreter.

This is not a surprising failure of "JIT in general". It is a specific outcome
of this project's execution model:

- the interpreter is already a **threaded interpreter**
- blocks are already **predecoded**
- dispatch overhead is already low
- superopcode / concat-block work further reduces the remaining dispatch cost

So a naive copy-and-patch JIT only removes a relatively small amount of
remaining dispatch overhead. If the copied machine code still preserves most of
the per-op handler scaffolding, the JIT has very little headroom to win.

## Baseline JIT Strategy Implemented

The implemented baseline JIT follows this structure:

1. Build a two-pass stencil extraction pipeline from the existing opcode
   implementation set.
2. Discover opcode handlers dynamically from compiled core objects.
3. Generate stencil kernels and extract copyable machine code plus relocation
   metadata.
4. At runtime, compile a `BasicBlock` by:
   - selecting one stencil per decoded op
   - copying each stencil into a JIT buffer
   - patching predecoded `DecodedOp` qwords into placeholder immediates
   - fixing branch/helper/rodata relocations
   - stitching exits together with block-local branches and veneers
5. Replace `BasicBlock::entry` with the JIT entry when compilation succeeds.

In concrete terms, the baseline implementation has these properties:

- **No SSA IR**
- **No register allocator**
- **No block-level instruction selection**
- **No real cross-op optimization**
- only limited post-pass peephole cleanup

It is therefore best viewed as a **template copier with relocation repair**,
not as a traditional optimizing JIT.

## Why Baseline Performance Loses

The hotspot profiles and annotated JIT block disassembly make the reason fairly
clear.

### 1. The interpreter baseline is already hard to beat

The current interpreter is not a naive fetch/decode/dispatch loop. It already
has:

- predecoded blocks
- direct threaded dispatch
- cached block lookup
- superopcode and block-concat support

That means the remaining dispatch cost is already relatively small. A JIT that
does not also improve **code quality inside each op** does not have much room
to win.

### 2. The copied stencils preserve too much handler scaffolding

The current JIT removes the outer dispatch chain, but most copied stencils
still look like standalone handler bodies:

- each hot op still has its own prologue/epilogue shape
- many hot stencils still materialize large stack frames
- slow-path setup is still present inside each copied stencil
- helper call / restart / fault glue is still preserved almost verbatim

So although dispatch is reduced, much of the original handler framework remains
intact in the emitted machine code.

### 3. Patched qwords still get decoded at runtime

The current patch model packs predecoded fields into qwords and patches those
constants into the stencil. That avoids loading fields from `DecodedOp` memory,
but many stencils still immediately do:

- `movz/movk/movk/movk`
- then `ubfx` / `lsr` / `and`
- then use the unpacked field

This means the runtime still pays a large amount of field-unpacking cost even
though the values were already known at decode time.

### 4. Memory and branch-heavy ops remain template-heavy

CoreMark hotspots are dominated by things like:

- `Mov_Load_*`
- `Mov_Store_*`
- `Jcc_*`
- `Imul_*`
- shift/group ops

These are exactly the kinds of handlers where:

- memory fast/slow path glue
- page/TLB checks
- branch exit glue
- per-op field decoding

still dominate the copied stencil. In practice, the JIT often ends up
executing "compiled handler scaffolding" rather than a significantly more
specialized fast path.

## Practical Conclusion

Under this project's current architecture, **pure copy-and-patch is not enough
to guarantee a speedup**.

This does **not** mean the work was wasted. The baseline implementation is
still valuable because it established:

- a JIT code cache
- relocation handling for helper calls and rodata
- block stitching infrastructure
- JIT block dumping / profiling / hotspot attribution
- a concrete proof of what this architecture can and cannot optimize

But the evidence now strongly suggests that the next performance-oriented step
should not be "more of the same copy-and-patch". The more promising direction
is to move toward a compiler-assisted approach that gives the optimizer a view
of larger traces or block bodies, such as:

- LLVM-based block / trace compilation
- generated block-level C++ trace functions
- or another approach that enables real constant propagation and cross-op
  optimization

The current copy-and-patch pipeline should therefore be treated as:

- a baseline JIT experiment
- a correctness and infrastructure milestone
- and a stepping stone toward a more optimizing design

## Recommended Reading of the Rest of This Document

The remaining sections still describe the design motivation and mechanics of
the copy-and-patch approach. They remain useful as:

- rationale for the baseline implementation
- documentation of stencil extraction and patching strategy
- background for future JIT infrastructure work

However, performance expectations should now be interpreted in light of the
results above: this design was successful as a baseline experiment, but not as
the final performance solution.

## Goal

The goal is to build a true copy-and-patch JIT by directly extracting copyable machine code stencils based on the existing `LogicFunc` system.

This proposal has several clear requirements:

- No longer follow the "offline block matcher / offline generation of whole block handlers" approach
- JIT generation should simply copy existing machine code and patch a small number of holes
- Fields already decoded in `DecodedOp` should be directly patched into the code, rather than loaded from `op` at runtime
- The generated codegen should be as close as possible to handwritten fast paths, rather than wrapping another interpreter layer
- The proposal must be implementable incrementally; it does not require covering all opcodes at once

This document assumes macOS AArch64 as the primary target platform, as current hotspot analysis and codegen work are mainly on this side.

## Non-Goals

The first phase does not pursue:

- SSA IR
- General-purpose register allocator
- Cross-op global optimization
- Speculative deopt framework
- Complex OSR

The first phase goal is a "high-quality template copier":

- Single-op stencil
- Block-level concatenation
- Small number of patch points
- Precise reuse of constants known from the decode phase

## Core Idea

The current execution chain can be roughly viewed as:

1. Decode into `DecodedOp`
2. `DispatchWrapper<Target>` reads `DecodedOp`
3. Call `LogicFunc`
4. Finalize according to `LogicFlow`

The goal of copy-and-patch JIT is not to reuse the entire `DispatchWrapper`, but to split each opcode into:

- A `StencilKernel` that can be compiled into a machine code template
- A set of statically described patch points
- A unified block-level glue that chains multiple stencils together

In other words, the future execution chain will become:

1. Decode into `DecodedOp`
2. Select `StencilId` based on opcode / specialization
3. Copy stencil machine code to code buffer
4. Use `DecodedOp` fields to patch holes
5. Append unified exit / branch / restart glue at block tail

## Why Start from LogicFunc

The closest "primitive that can be directly templated" in the existing code is `LogicFunc`, not `HandlerFunc`.

Reasons:

- `HandlerFunc` contains dispatch framework, tail calls, and `LogicFlow` finalization
- `LogicFunc` is the actual semantic body
- Many optimizations have already subdivided numerous opcodes into specialized implementations like `ModReg` / `Opsize32` / `NoFlags`
- These specialized `LogicFunc`s themselves are very much like stencil candidates

Therefore, the suggestion is:

- Continue to retain `LogicFunc`
- But add a new layer of "stencil-extractable kernel entry"
- Do not extract templates directly from `DispatchWrapper<T>`

## Overall Structure

The following layers are recommended.

### 1. Stencil Kernel

Each JIT-able opcode variant provides a dedicated kernel:

```cpp
template <typename PatchState>
void OpAdd_EvGv_Kernel(PatchState& s);
```

Here `PatchState` is not a runtime object, but to allow the same C++ code to:

- Be compiled as a stencil extraction target
- Also serve as an interpreter reference implementation

However, when actually entering JIT, it is not recommended to keep `PatchState` as a memory object. A better approach is:

- Use it during the extraction phase to guide the compiler to produce stable machine code
- Only use patched machine code during the runtime phase

### 2. Stencil Blob

Each kernel is compiled and extracted into:

- `code bytes`
- `patch points`
- `clobber information`
- `exit kind`

That is:

```cpp
struct StencilBlob {
    const uint8_t* code;
    uint32_t code_size;
    const PatchDesc* patches;
    uint16_t patch_count;
    StencilExitKind exit_kind;
    RegMask clobbers;
};
```

### 3. JIT Block Builder

The block builder's work is:

- Iterate through `BasicBlock::FirstOp() ... Sentinel()`
- Select stencil for each op
- Copy to code buffer
- Patch according to the op's `DecodedOp` content
- Finally append block exit glue

### 4. Runtime Code Object

JIT blocks need their own native entry:

```cpp
struct JitCodeBlock {
    void* entry;
    uint32_t code_size;
    BasicBlock* owner;
};
```

Then:

- `BasicBlock::entry` points to JIT code
- When compilation fails or is unsupported, fall back to `FirstOp()->handler`

## Key Design: Directly Patch DecodedOp Fields

This is the core of this proposal.

Currently `DecodedOp` has already decoded a large amount of information in advance:

- `next_eip`
- `len`
- `modrm`
- `prefixes`
- `meta`
- `ext.data.imm`
- `ext.data.ea_desc`
- `ext.data.disp`
- `ext.control.target_eip`

Many of these fields are repeatedly loaded in the interpreter to decide behavior.  
The goal of copy-and-patch JIT is:

- For constants known at decode phase, convert them to machine code immediates as much as possible
- Do not fetch from `DecodedOp` at runtime

### Fields That Can Be Directly Patched

The first batch recommended for direct patching:

- `imm`
- `disp`
- base/index/scale/segment decoded from `ea_desc`
- `target_eip`
- `next_eip`
- `len`
- register indices derived from `modrm`
- fixed patterns derived from `prefixes`
- `meta.no_flags`
- `meta.has_mem`
- `meta.has_imm`

### Fields Not Recommended for Direct Patching

These fields are more suitable as block-level context rather than per-op patch:

- `EmuState*`
- `MicroTLB`
- `flags_cache`
- Current guest register base

These are better placed in约定 registers throughout the entire JIT block.

## Register Convention

To make stencils composable, there must be a fixed block ABI.

On AArch64, it is recommended to fix several categories of registers from the start.

### Long-Lived Registers

- `x19`: `EmuState*`
- `x20`: guest GPR base or directly `state->ctx.regs`
- `x21`: `flags_cache`
- `x22`: `instr_limit`
- `x23`: `MicroTLB` struct value or pointer
- `x24`: current block metadata / slow-path helper scratch

### Temporary Registers

- `x9-x15`
- `w9-w15`

### Return Convention

Stencils within a block do not directly `ret`, but jump to unified glue:

- `continue`
- `exit_current_eip`
- `exit_next_eip`
- `restart_memory`
- `retry_memory`
- `exit_to_branch`

This way each stencil only needs to:

- Perform its own semantics
- Jump to a fixed label / trampoline when needed

Without having to repeat the finalization framework itself.

## Stencil Type Layering

Not all opcodes are suitable for the same kind of stencil.

The first phase is recommended to be divided into 4 categories.

### A. Pure Register Stencils

For example:

- `OpTest_EvGv_32_ModReg`
- `OpCmp_EvGv_32_ModReg`
- `OpGroup1_EvIb_Add_32_NoFlags_ModReg`
- `OpPush_Reg32_*`
- `OpPop_Reg32_*`

Characteristics:

- No memory access
- Few patch points
- Easiest to obtain high-quality codegen

This is the first batch that should be supported.

### B. Fixed Immediate / Fixed Register Stencils

For example:

- `mov reg, imm`
- `add r32, imm8`
- `test r32, r32`

Characteristics:

- After decode, almost all control fields can be patched as immediates
- After generation, very close to handwritten assembly

### C. Memory Address Formation Stencils

For example:

- `lea`
- `movzx/movsx`
- `mov load/store`

This category should:

- base/index reg selection
- scale
- disp
- segment base rules

Be patched in advance to the narrowest path as much as possible.

### D. Control Flow Stencils

For example:

- `Jcc`
- `Jmp rel`
- `Call rel`

This category needs to be designed together with block exit / chaining glue.

## Recommended Extraction Method

It is recommended not to hand-write assembly templates, nor to manually maintain byte sequences.  
A more reliable approach is:

1. Write dedicated stencil kernels in C++
2. Compile with Clang into target platform objects
3. Use scripts to extract symbol machine code
4. Identify patch points using pre-embedded markers

This is also the most natural route for copy-and-patch.

### Patch Marker Design

It is recommended to provide dedicated helpers for each type of patch point:

```cpp
uint32_t PatchImm32(PatchToken<Kind::Imm32>);
uint64_t PatchAbs64(PatchToken<Kind::Abs64>);
int32_t PatchRel32(PatchToken<Kind::Rel32>);
```

During extraction:

- The compiler will materialize these constants into the code
- Encode token id with a magic value that rarely appears naturally
- Scripts scan machine code to locate these magic constants

For example:

- `0xC1A00001 + patch_id`
- `0xD1A00001 + patch_id`

Then record them into a patch table.

### Why Not Rely on DWARF / LLVM MIR

It's possible, but not recommended for the first phase.

Reasons:

- Toolchain coupling is too heavy
- High development cost
- Unfriendly for daily iteration

Magic immediate scanning is naive but more stable and easier to debug.

## Code Generation Requirements

To ensure good generated stencil codegen, kernels must follow some constraints.

### 1. Avoid General Wrapper Logic

Kernels should not contain:

- Large `switch(LogicFlow)` framework
- Generic `DispatchWrapper`
- Generic helpers that re-read `DecodedOp`

Kernels should only do the opcode body.

### 2. Use Specialized Variants as Much as Possible

Do not extract stencils from general entry points like `OpCmp_EvGv`, but from:

- `OpCmp_EvGv_32_ModReg`
- `OpLea_32`
- `OpGroup1_EvIb_Add_32_NoFlags_ModReg`

These already narrowed implementations.

### 3. Move Decode Information Forward as Template Parameters or Patches

For example:

- `mod=3`
- `opsize=32`
- `no_flags=true`

Should be determined at stencil selection phase, not left to runtime.

### 4. Let Block Builder Handle Glue

Kernels are not responsible for:

- Interpreter chaining calls
- Slow-path scheduling protocol
- Whole-block exit

It is only responsible for writing results to fixed registers / fixed state, then jumping to glue.

## Recommended JIT ABI

It is recommended that JIT block entry use an independent ABI, rather than reusing the `HandlerFunc` ABI.

Current `HandlerFunc`:

```cpp
int64_t(ATTR_PRESERVE_NONE*)(EmuState*, DecodedOp*, int64_t, MicroTLB, uint32_t, uint64_t)
```

This ABI is suitable for the interpreter, but not ideal for JIT blocks, because:

- `DecodedOp*` is no longer the main input after JIT
- `branch` is more like internal temporary storage
- `flags_cache` should be resident in a register

It is recommended to add:

```cpp
using JitEntryFunc = int64_t(*)(EmuState* state, int64_t instr_limit);
```

Or:

```cpp
using JitEntryFunc = int64_t(*)(EmuState* state, JitRuntimeContext* ctx, int64_t instr_limit);
```

Where `JitRuntimeContext` can hold:

- `MicroTLB`
- Slow path thunk table
- Current block pointer

This is cleaner than continuing to reuse `HandlerFunc`.

## Slow Path Design

Copy-and-patch JIT should not attempt to inline all complex cases.

It is recommended to retain a set of common helpers:

- `JitSlow_ReadMem8/16/32`
- `JitSlow_WriteMem8/16/32`
- `JitSlow_RestartMemory`
- `JitSlow_RetryMemory`
- `JitSlow_ResolveBranch`
- `JitSlow_CommitExitCurrent`
- `JitSlow_CommitExitNext`

Stencils only retain:

- Hot path
- Fail branch to helper

This will significantly reduce the number of stencils.

## Control Flow Approach

### Direct Jumps

For `Jmp rel` / `Jcc rel`:

- `target_eip` is known at decode time
- Block builder can first try to look up `block_cache`
- When hit, directly patch to block entry jump
- When missed, jump to `ResolveBranch` helper

### Conditional Jumps

`Jcc` stencils are recommended to be split into two parts:

- Condition judgment kernel
- Taken/not-taken glue

This way:

- The condition body can be very short
- Branch target patch can be handled separately by block builder

### Block Chaining

This can later evolve to:

- Initially point to resolver thunk at generation time
- After running for a while, directly fill in known block entry

This is a natural extension of copy-and-patch, but does not need to be completed in the first phase.

## Relationship with Existing DecodedOp

The first phase is recommended to keep `DecodedOp` unchanged.

It is still responsible for:

- Interpreter fallback
- Decode intermediate result storage
- Block stats / profiling input

But JIT builder can derive tighter patch input from `DecodedOp`:

```cpp
struct StencilPatchData {
    uint32_t imm;
    uint32_t disp;
    uint32_t next_eip;
    uint32_t target_eip;
    uint32_t ea_desc;
    uint8_t modrm;
    uint8_t prefixes;
    uint8_t meta;
};
```

In other words:

- `DecodedOp` is the decode product
- `StencilPatchData` is the codegen input

This way, if `DecodedOp` continues to evolve later, the JIT patch layer will not be overly coupled.

## Stencil Selection Granularity

It is recommended to use "logical opcode variant" as the stencil key, rather than raw x86 opcode.

For example:

- `StencilId::Test_EvGv_32_ModReg`
- `StencilId::Jcc_E_Rel8`
- `StencilId::Movzx_Byte`
- `StencilId::Lea_32`

Do not directly use:

- `0x85`
- `0x74`

Because the same opcode may correspond to completely different code shapes after specialization.

## Generation Flow

It is recommended to proceed in three steps.

### Step 1. Dynamic Stencil Discovery and Generation

Instead of manually selecting and writing every single stencil wrapper, dynamically scan the compiled interpreter library (`libfibercpu.dylib`):
1. Extract all `op::*` functions that the interpreter instantiated inside `DispatchWrapper<T>`.
2. Generate a `stencil_kernels.generated.cpp` containing an `ExtractKernel<T>` explicitly instantiated for each discovered `op::*` target.
3. Compile this generated C++ file to an object file (`stencil_kernels.o`).

- `OpTest_EvGv_32_ModReg`
- `OpCmp_EvGv_32_ModReg`
- `OpGroup1_EvIb_Add_32_NoFlags_ModReg`
- `OpGroup1_EvIb_Add_32_Flags_ModReg`
- `OpPush_Reg32_*`
- `OpPop_Reg32_*`

### Step 2. Build Stencil Extractor

Write Python extraction scripts attached to the build system:
1. `gen_jit_stencils.py`: Scans `libfibercpu.dylib` using `nm`, parses the mangled `DispatchWrapper` symbols, and generates `stencil_kernels.generated.cpp`.
2. `extract_stencils_obj.py`: Parses the disassembled `stencil_kernels.o` using `llvm-objdump` (or `objdump`).
   - Find the boundaries and machine code bytes for each `<__ZN8fiberish3jit13ExtractKernel...>` function.
   - Scan for the magic patch markers (e.g. `movz/movk` sequences mapping to `PatchMagic32`).
   - Extract `stencils.generated.inc` containing byte arrays and patch descriptors.

### Step 3. Build Block Builder

Block builder initially only supports:

- Pure register stencil concatenation
- Fall back directly to interpreter block when unsupported

This allows the fastest validation of the entire chain.

## Recommended Data Structures

```cpp
enum class PatchKind : uint8_t {
    Imm32,
    UImm16,
    UImm8,
    Disp32,
    TargetEip32,
    NextEip32,
    AbsPtr64,
    Rel32,
    GprIndex,
    SegmentIndex,
};

struct PatchDesc {
    uint32_t offset;
    PatchKind kind;
    uint16_t aux;
};

struct StencilDesc {
    const uint8_t* code;
    uint32_t code_size;
    const PatchDesc* patches;
    uint16_t patch_count;
    uint16_t id;
    uint32_t flags;
};
```

`aux` can be used to express:

- Which register field
- Which helper thunk
- Which control flow target slot

## Block Builder Patch Rules

### Immediates

Directly from:

- `GetImm(op)`

Patch to target offset.

### Address Formation

From:

- `ea_desc`
- `disp`

Decode:

- base reg index
- index reg index
- scale
- segment

Then patch to stencil reserved bits.

### Control Flow

From:

- `GetControlTargetEip(op)`

Generate:

- Known block entry address patch
- Or slow helper target

### next_eip

From:

- `op->next_eip`

Patch to immediate required by exit glue.

## Key Performance Trade-offs

If the goal is good codegen, the following points are important.

### 1. Do Not Retain `DecodedOp*` Dependency in Stencils

If stencils still frequently `ldr [op + offset]`, the benefit will be significantly reduced.

### 2. Do Not Make Patch Points Indirectly Read from Memory Tables

Patch results should directly land on machine code immediates / embedded addresses.

### 3. Share Long-Lived State Within Blocks

For example:

- `flags_cache`
- `state`
- `regs base`

Otherwise, each stencil must re-materialize, losing much benefit.

### 4. Hot Path Only Jumps to Local Labels

Only slow paths `call` helpers.

### 5. Prioritize Supporting Specialized Opcodes with Already Clean Code Shapes

This is the easiest part to obtain "still beautiful after copying".

## Relationship with SuperOpcode

SuperOpcode and copy-and-patch are not mutually exclusive.

A better relationship should be:

- SuperOpcode continues as a profiling guidance tool
- But the JIT layer does not necessarily generate "A+B" fused stencils
- It can also only be used to guide:
  - Which ops are worth prioritizing for stenciling
  - Which block combinations are worth prioritizing for support

In other words:

- `SuperOpcode` is a profile / grouping signal
- `Stencil` is the codegen unit

Do not forcibly bind the two into the same thing.

## Risk Points

### 1. Unstable Compiler Output

Machine code for the same kernel may change across different Clang versions.

Mitigation:

- Patch marker scanning should not rely on fixed instruction sequences
- Only rely on magic immediates

### 2. Helper Calls Pollute Code Shape

If kernels frequently fall through to helpers, benefits will be reduced.

Mitigation:

- Start with pure register stencils first
- Memory stencils can be layered later

### 3. ABI Convention Instability

If block-level register protocol changes frequently, all stencils must be re-extracted.

Mitigation:

- First freeze a version of AArch64 block ABI
- Then only add, never break

### 4. High Debugging Difficulty

Code generated by copy-and-patch is less intuitive than the interpreter.

Mitigation:

- Generate sidecar dump for each JIT block
- Be able to print stencil sequences and patch results

## Debugging Recommendations

It is recommended to generate the following debugging artifacts:

- `jit_block_<eip>.bin`
- `jit_block_<eip>.json`
- `jit_block_<eip>.s`

At minimum, include:

- Which stencils were used
- Patch list for each stencil
- Final code address and size
- Corresponding original `DecodedOp` summary

## Phased Implementation Recommendations

### Phase 0

First build the framework without enabling execution:

- Stencil descriptor
- Extractor
- Patch metadata
- Code buffer allocator

### Phase 1

Only support pure register blocks:

- `Test/Cmp/Add ModReg`
- `Push/Pop`

Fall back to interpreter when unsupported.

### Phase 2

Add simple control flow:

- `Jcc`
- `Jmp rel`

### Phase 3

Add load/store and address formation:

- `Lea`
- `Movzx/Movsx`
- `Mov load/store`

### Phase 4

Add block chaining / direct patch backedge.

## Recommended First Batch of Implementation Targets

Based on current hotspots and implementation difficulty, the recommended first batch of stencil priorities:

1. `op::OpTest_EvGv_32_ModReg`
2. `op::OpCmp_EvGv_32_ModReg`
3. `op::OpGroup1_EvIb_Add_32_NoFlags_ModReg`
4. `op::OpGroup1_EvIb_Add_32_Flags_ModReg`
5. `op::OpJcc_E_Rel8`
6. `op::OpJcc_NE_Rel8`
7. `op::OpPush_Reg32_*`
8. `op::OpPop_Reg32_*`

These share common characteristics:

- Hot
- Clear code shape
- Few patch points
- Suitable for validating framework correctness

## Final Recommendations

If the goal is to build a copy-and-patch JIT with good codegen performance, the most important thing is not to "directly compile the entire interpreter over", but rather:

- Start from already specialized `LogicFunc`
- Directly patch `DecodedOp` decode results into machine code constants
- Share ABI and glue within blocks
- First do the narrowest, hottest, most assembly-template-like batch

In one sentence:

Not "JIT executes `DecodedOp`", but "decode produces patch data, JIT executes patched stencil machine code".

---

## Appendix A: Research Notes and Experimental Verification

### A.1 Field Access Scanning and Two-Pass Generation Approach

**Problem**: How to know which `DecodedOp` fields are actually used, thereby deciding which need patch points generated?

**Solution**: Two-pass scanning approach

#### Pass 1: Generate Reference Code and Scan

Compile a reference implementation using `DecodedOp*` parameters, then scan the generated assembly to identify which field offsets are accessed:

```asm
; Pass 1 output for OpTest_EvGv_32_ModReg
ldrb w8, [x21, #13]    ; Only accesses modrm (offset 13)
; Does not access meta (offset 15) or imm (offset 16)

; Pass 1 output for OpGroup1_EvIb_Add_32_NoFlags_ModReg
ldrb w8, [x21, #15]    ; Accesses meta (checking has_imm)
tbnz w8, #2, LBB3_2
ldrsb w8, [x21, #16]   ; Conditionally accesses imm (offset 16)
ldrb w9, [x21, #13]    ; Accesses modrm (offset 13)
```

#### Identifying DecodedOp* Pointer Registers

Key observation: `DecodedOp*` pointers can be identified through **fixed offset access patterns**:

| Field | DecodedOp offset | Assembly pattern |
|-------|-----------------|------------------|
| modrm | 13 | `ldrb [xN, #13]` |
| prefixes | 14 | `ldrb [xN, #14]` |
| meta | 15 | `ldrb [xN, #15]` |
| ext.data.imm | 16 | `ldr/ldrsb [xN, #16]` |

Meanwhile, other registers have different access patterns:
- `EmuState*` (e.g., `x20`): `ldr [x20, xR, lsl #2]` (indexed access to regs array)
- `DecodedOp*` (e.g., `x21`): `ldrb [x21, #13]` (fixed offset access to fields)

#### Pass 2: Generate Optimized Stencils Based on Usage Mask

Generate `asm volatile` patch points only for fields actually accessed in Pass 1:

```cpp
// Scanning result example
struct FieldAccessMask {
    bool modrm_used = true;
    bool meta_used = true;
    bool imm_used = false;  // OpTest does not use imm
};

// Pass 2: Only generate patch points for used fields
template <LogicFunc Target, bool PatchModRM, bool PatchMeta, bool PatchImm>
Pass2_OptimizedStencil(...) {
    if constexpr (PatchModRM) {
        asm volatile("movz %w0, %1" : "=r"(modrm_val) : "n"(0xD1));
    }
    if constexpr (PatchImm) {
        // OpTest will not generate this code, saving 2 instructions
        asm volatile("movz %w0, %1" : "=r"(imm_val) : "n"(0x05));
        asm volatile("movk %w0, %1, lsl #16" : "+r"(imm_val) : "n"(0x00));
    }
}
```

#### Experimental Result Comparison

| Approach | OpTest (imm not used) | OpAdd (imm used) |
|----------|----------------------|------------------|
| Full Patch (all fields) | `mov` + `mov` + `mov` + `movk` (4 instructions) | `mov` + `mov` + `mov` + `movk` (4 instructions) |
| Optimized (only used fields) | `mov` + `mov` (2 instructions, **50% savings**) | `mov` + `mov` + `mov` + `movk` (4 instructions) |

#### Implementation Steps

1. **Pass 1 Compilation**: Compile stencil reference implementation with `DecodedOp*` parameters
2. **Static Analysis**: Scan assembly for `ldr{b,h,sb} [xN, #offset]` patterns
3. **Field Mapping**: Map offsets to `DecodedOp` fields based on offset
4. **Pass 2 Generation**: Use `if constexpr` to generate `asm volatile` only for used fields

**Notes**:
- Conditional access (e.g., `if (HasImm(op)) return op->ext.data.imm;`) should be handled conservatively, counted as "used"
- `__attribute__((const))` can help the compiler remove code for unused fields, but may over-optimize
- `asm volatile` ensures patch points are always retained, even if fields are not used

### A.2 Impact of `__attribute__((const))` on Patch Points

Experiments verified whether `__attribute__((const))` can remove unused patch points:

| Approach | Test (imm not used) | Add (imm used) |
|----------|---------------------|----------------|
| `asm volatile` | `mov w8, #209` + `mov w9, #0x5555` + `movk` (retained) | `mov w8, #193` + `mov w8, #5` + `movk` (retained) |
| `asm` non-volatile + `const` | `mov w8, #209` (imm removed) | **Entire function optimized to `eor x0, x25, x21; ret`** |
| Pure constexpr | Normal codegen (no asm) | **Entire function optimized away** |

**Conclusion**:
- `asm volatile` ensures patch points are always retained, suitable for copy-and-patch JIT
- `__attribute__((const))` + non-volatile asm can remove unused patch points, but may over-optimize
- Recommended to use `asm volatile` and accept "redundant" patch points, or use two-pass approach to decide code shape at stencil selection phase

### A.3 Comparison of Various Patch Approaches

| Approach | Patch Point Location | Code Quality | Extractability | Evaluation |
|----------|---------------------|--------------|----------------|------------|
| **PatchToken (volatile)** | Stack store/load | Medium | Low | Compiler generates `strb/str` to stack, patch requires changing stack initialization |
| **Asm Placeholder (movz/movk)** | Immediate | Medium | **High** | Explicit `movz/movk` sequence, easy to scan and patch |
| **Literal Pool (ldr =)** | Literal pool | Medium | Medium | `ldr w8, Ltmp` + data area, requires relocation |
| **Data Slot (hybrid)** | GOT + ldr | Low | Medium | Extra `adrp/ldr` indirection layer, but data slots independently patchable |

**Recommended Approach**: `movz/movk` inline assembly placeholders

```cpp
template <uint16_t ModRMVal>
__attribute__((always_inline)) static inline uint32_t patchable_modrm() {
    uint32_t result;
    asm volatile("movz %w0, %1" : "=r"(result) : "n"(ModRMVal));
    return result;
}
```

Extractor scans `movz`/`movk` instructions, extracting immediates as patch points.

---

## Appendix B: Development Checklist

This checklist tracks the implementation of the JIT generation pipeline and integration:

### Phase 1: JIT Framework & Stencil Extractor
- [x] Define JIT stencil data structures (`libfibercpu/jit/stencil.h`)
  - `PatchKind`, `PatchDesc`, `StencilDesc`, `StencilBlob`
  - Magic immediate patch token helpers (`PatchMagic32`, `PatchMagic64`)
- [x] Implement dynamic generation script (`scripts/gen_jit_stencils.py`)
  - Scan `libfibercpu.dylib` using `nm` to discover `fiberish::op::*` templates from `DispatchWrapper`.
  - Dynamically generate C++ kernels file (`stencil_kernels.generated.cpp`).
- [x] Implement Python extraction script (`scripts/extract_stencils_obj.py`)
  - Parse compiled kernels using `llvm-objdump` / `objdump`.
  - Extract machine code for each stencil.
  - Scan for magic patch markers and emit `PatchDesc` arrays.
  - Generate `stencils.generated.inc`.
- [x] Integrate pipeline into CMake
  - Ensure kernels are generated, compiled, and extracted dynamically as part of `fibercpu_jit_stencils` target.
- [ ] Fix symbol parsing and extraction bugs in `extract_stencils_obj.py` (Currently WIP).

### Phase 2: Block Builder & Integration
- [x] Implement baseline JIT Block Builder (`libfibercpu/jit/block_builder.h` / `.cpp`)
  - Maps decoded block entries to extracted stencils.
  - Allocates code from a dedicated JIT code cache.
  - Applies qword patch points, branch relocations, and rodata relocations.
  - Builds block-local veneers and runtime metadata.
- [x] Integrate into execution flow (`DecodeBlock`, concat-block path)
  - `JitCodeBlock` ownership is wired through `BasicBlock::jit_code`.
  - Fallback to interpreter when stencil selection or compilation fails.
  - Concat blocks also go through the same JIT compile path when enabled.
- [x] Add build-time kill switch
  - `FIBERCPU_ENABLE_JIT` exists as a CMake option.
  - Default is `OFF` because baseline performance is currently worse than the interpreter.

### Phase 3: Verification
- [x] Validate generated JIT machine code using existing testing and dump tooling.
- [x] Test JIT execution on simple instructions and larger workloads.
- [x] Measure performance and verify correctness against reference interpreter.
- [x] Record baseline outcome
  - Correctness is substantially in place.
  - CoreMark on M3 Max is about `~1950`, below the interpreter's `~2250`.
  - Therefore the baseline JIT is currently an experimental path, not the default runtime.
