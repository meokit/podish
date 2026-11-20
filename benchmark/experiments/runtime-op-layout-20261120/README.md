# Compact runtime op experiment

The ARM64 build has a 32-byte, 16-aligned DecodedOp, a 48-byte block header, and
one sentinel slot. Blocks eligible for compilation can also keep eight bytes of
scalar handler pointer per instruction.

## Candidate layout

| Offset | Bytes | Runtime field |
| --- | ---: | --- |
| 0 | 8 | Compact handler pointer |
| 8 | 4 | Next guest EIP |
| 12 | 1 | Length including absorbed NOPs |
| 13 | 1 | ModRM or opcode register field |
| 14 | 2 | Packed effective address |
| 16 | 4 | Immediate or direct branch displacement |
| 20 | 4 | Memory displacement |

The effective address packs base and index ordinals into four bits each,
including the zero register. It keeps two scale bits, three original segment
bits and three resolved segment bits. Segment bases and GPR values are loaded
from CPU state. For implicit-address operations the field can also carry the
segment override.

The compact handler policy supplies semantics currently read from prefixes and
metadata. Generate the required axis combinations per kernel. Those axes include
operand width, address width, relevant REP or mandatory SIMD prefix, group or
operand form where required, and dead-flags behavior. ModRM, EA registers,
immediates and displacements can remain runtime values in this first layout.
A build of the required variants must cover every published instruction form.
A cold adapter can expand its bound compact information into a rich instruction
for a legacy semantic helper. Its published entry and dispatch still use the
compact ABI.

Current OperandSpec::Exact and OperandSpec::Profile constraints constrain
loads of existing fields with compiler assumptions. The compact API needs
accessors that supply values before code generation so those fields can
leave the runtime array.

## EIP and chaining

Keep absolute next EIP and the length in the first migration. Fault handling,
memory replay, x87 instruction pointers, interrupt observers and relative CALL
return values use an exact address. Length remains eight bits because the
decoder may absorb NOPs up to 255 bytes per slot. Direct-JMP concatenation
removes the jump and appends potentially noncontiguous slots. A later PC
compression needs per-segment anchors or validated per-slot block-relative
offsets.

A direct relative branch can derive its full target from next EIP and its
displacement. Bind the existing short, near16 and near32 rules to the handler.
The near16 form applies the 16-bit wrap. Indirect control flow continues to
compute the target from guest state.

For taken branches, derive the slot from the owning block and the fixed stride.
The regular entry and tail chain already update state->last_block. A sparse
branch-cache footer can use one 64-bit mask and one pointer per eligible slot.
Compute the rank of the current slot in the mask to select the cached pointer.
Check the pointer's validity and full target EIP before chaining. Concatenation
rebuilds the mask from its resulting slot order, drops removed JMP slots, and
retains independent entries for surviving interior Jccs. The fallthrough or
branch-carrying sentinel has its own link. Handle it before computing a mask
rank because its index can be 64.

The stack-only Step and SMC paths need explicit scratch ownership with local
edge storage. Install that context around execution and keep it out of the
block cache. Any pending replay uses a compact scalar entry for the current
slot. A compiled prefix cannot put its legacy generic handlers back into
compact slots. For concatenation and replay, keep the selected compact unfused
handler or select it by re-decoding on the cold path.

## Code generation and estimated storage

The standalone probe compiled with Clang 21 on Apple ARM64 with write-back pin
arguments. The 24-byte candidate is 8-aligned. All 2268 base, index, scale and
segment combinations round-tripped, and sparse index rank matched across all
64 slots. The dispatch sequence is still two instructions. It uses
`ldr x0, [x21, #24]!` and `br x0`, with 32 as the previous stride. The
edge rank routine adds three instructions for dividing an exact pointer
difference by 24. The sequential dispatch does no edge rank calculation.

| Existing v3 profile | Blocks | Ops | Old bytes | Estimated compact bytes | Saved |
| --- | ---: | ---: | ---: | ---: | ---: |
| CoreMark | 2,446 | 15,656 | 696,672 | 593,496 | 14.81% |
| Webpack | 213,142 | 1,404,508 | 61,995,616 | 52,870,504 | 14.72% |

The estimate counts the 48-byte block header, one sentinel per block, and
sparse mask and pointer storage for all control-flow extension slots. It
excludes cache maps and allocation overhead. Keeping eight replay bytes for
every instruction gives 12.55% and 12.46% savings, respectively. The dumps have
153 and 10,634 PC discontinuities across slots. These are estimates on existing
profiles. The ARM64 probe checks the dispatch sequence and packed shape, without
modeling full workload performance.

## Migration checks

1. Isolate native block dump and debug snapshots from managed pointer arithmetic.
   Keep the v3 file encoding and regenerate linked native artifacts with the
   new ABI when the slot type changes.
2. Separate decode and specialization staging from published storage. Keep
   DecodedOperandShape independent of either slot layout. Run DFE, fusion
   selection and mandatory lowering on rich input before compact publication.
3. Convert scalar and fused runtime dispatch, sentinel, branch helpers, hooks
   and memory replay as a single ABI unit. Cover invalid opcodes, Step, write
   faults, near16 wrap, SMC invalidation and concurrent code-cache flush.
4. Give async JIT a transient recipe and instruction-byte snapshot. Check
   generations and SMC revisions before publishing. Revalidation compares
   immutable semantics and disregards the mutable branch-cache pointers.
5. Verify all mandatory variants and fallback adapters then rerun CoreMark,
   Webpack and SuperTuxKart with code-cache bytes, handler size, taken branches
   and frame timing.

## Reproduce

Run from the repository root with paths to a native CMake build's generated
headers and SIMDE checkout.

```sh
mkdir -p artifacts/runtime-op-layout
clang++ -std=c++23 -O3 -march=native -DFIBERCPU_GPR_PIN_MODE=4 \
  -Ilibfibercpu/src -I"$NATIVE_BUILD/generated" -isystem "$SIMDE_DIR" \
  benchmark/experiments/runtime-op-layout-20261120/probe.cpp \
  -o artifacts/runtime-op-layout/probe
artifacts/runtime-op-layout/probe
python3 benchmark/experiments/runtime-op-layout-20261120/estimate.py \
  "$COREMARK_PROFILE/blocks.bin" "$WEBPACK_PROFILE/blocks.bin"
```

The profile inputs were structured-training/stats-fusion-v3 and
webpack-20261119/training-profile.
