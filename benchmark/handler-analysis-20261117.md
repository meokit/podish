# Handler performance and assembly audit

Base: `20771daeda8ec985f8b402a4741b69950a9508d9` (SMC fixes plus the
promoted 32-pair/32-four-op default). Measurements below were taken from
working-tree changes on this base, before the later tail-defer application.
No superopcode selection, SMC policy, or managed syscall implementation was
changed by this experiment.

## Environment and evidence

- Windows x64, AMD Ryzen AI MAX PRO 390, 12 cores / 24 logical processors.
- clang-cl 22.1.3, `x86_64-pc-windows-msvc`, Release native build.
- .NET SDK 11.0.100 / runtime 11.0.0, net10.0 CLI with `LatestMajor` roll-forward.
- Node 14; existing pinned Node-heavy image, tmpfs, 1 GiB container memory.
- Raw evidence: `artifacts/handler-perf-20771da/`. Each native candidate and its
  disassembly were saved before the next build. CLI directories are independent
  copies, not a mutable output directory shared with builds.
- Assembly comes from the actual DLL's exported handler-pointer registry and
  `llvm-objdump`, not an approximation compiled from an isolated source snippet.
  `inspect-handlers.py` records each handler RVA in `asm-*/handlers.json`.
- Saved baseline DLL SHA256: `dc7356edc392088fae541ad05036f5f003b91d035919efda2b4c3091eceb7697`.
  Final CLI DLL: `5a1e873d5bab4d8f558a6821166e4fed2faf8e1d379fd7359bfdb549d81ade9f`.
  Rebuilding changes image identity, but the final DLL's complete `.text` section
  is byte-identical to the two-change DLL used in the microbenchmarks (SHA256
  `74d889b23c01e3c3e2d1b7db64b02786a13fed1d2f1e99c85a3a5b74ffbceb22`).

## Retained change 1: actually enable the intended internal handler ABI

The original probe was `__has_cpp_attribute(preserve_none)`. This compiler reports
**0** for that probe but **1** for `__has_attribute(preserve_none)`. Consequently,
the original tail-dispatched handlers silently used the ordinary Windows ABI.

`decoder.h` now uses the GNU-function-attribute probe on Clang x64. Other targets
retain their previous selection. `ops.cpp::ChainToKnownBlock` must carry the same
attribute: without it, Clang correctly rejects its `musttail` edges as calling
convention mismatches. Public `X86_Run` and native callbacks keep the platform
ABI; only the internal handler function-pointer graph changes.

For the actual `OpMov_Load_Ecx` wrapper, baseline RVA `0x57af0`:

```asm
push r14
push rsi
push rdi
push rbx
; ... work ...
mov  rax, [rsp+58h]     ; flags cache
mov  r10d, [rsp+50h]    ; branch
; ...
mov  rsi, [rsp+48h]     ; micro-TLB addend
; ...
mov  [rsp+58h], rax
mov  [rsp+50h], r10d
mov  [rsp+48h], rsi
pop  rbx
pop  rdi
pop  rsi
pop  r14
jmp  r11
```

ABI-only RVA `0x51be0` has **no push/pop or RSP reference on its L1-hit path**.
State/op pointers and threaded state remain in registers, and dispatch is still
an indirect tail jump. MOV-store's original seven save/restore pairs shrink to
one pair; the remaining pair is discussed under the rejected experiment below.

## Retained change 2: resolve segment selection once, not at every memory access

The base register and segment override are invariant for a decoded operand.
Previously `GetMemSegmentBase` repeatedly selected DS versus SS and applied an
override while executing every generic memory handler.

`memdesc::PackEA` now stores the resolved segment number in spare bits 17..19.
Original override bits 14..16 are unchanged, preserving MOV-specialization
matching and tooling's existing field interpretation. Descriptor/DecodedOp size
does not grow. `GetMemSegmentBase` still reads the **live** segment base from the
CPU context, so changing FS/GS/DS/SS after translation remains valid.

ABI-only MOV-load, VA `0x180051c18` through `0x180051c3c`, selects the segment
using 14 instructions:

```asm
mov   ebx, eax
shr   ebx, 0eh
and   bl, 7
and   al, 3bh
cmp   al, 10h
sete  al
mov   r8b, 4
sub   r8b, al
test  bl, bl
movzx eax, r8b
movzx ebx, bl
cmove ebx, eax
movzx eax, bl
mov   r8d, [r12+rax*4+34h]
```

The retained two-change candidate reduces this to three instructions at
`0x18004ff48`:

```asm
shr eax, 0fh
and eax, 1ch
mov r8d, [r12+rax+34h]
```

Clang folds segment-index scaling into the shift/mask. EBP as an index (without
an SS-selecting base) still selects DS. Address-size truncation still precedes
segment-base addition. New `DecodedSegmentSelectionTests` cover 12 combinations
of 16/32-bit addressing, SIB/base/index shapes, overrides, and cached code reused
after a segment-base change. They pass both before and after the optimization.

### Actual L1-hit path counts

Counts follow execution from entry through the first tail jump with miss
branches not taken. These are x86 instructions, **not uops or cycle estimates**.

| Actual wrapper | Baseline | ABI only | ABI + decoded segment (retained) |
| --- | ---: | ---: | ---: |
| MOV-load ECX instructions | 63 | 47 | 36 |
| MOV-load push/pop instructions | 8 | 0 | 0 |
| MOV-load explicit RSP references | 6 | 0 | 0 |
| MOV-store EAX instructions | 72 | 52 | 41 |
| MOV-store push/pop instructions | 14 | 2 | 2 |
| MOV-store explicit RSP references, including stack adjustment | 8 | 2 | 2 |

Handler entry RVAs by version:

| Wrapper | Baseline | ABI only | Retained candidate |
| --- | --- | --- | --- |
| MOV-load ECX | `0x57af0` | `0x51be0` | `0x4ff10` |
| MOV-store EAX | `0x54380` | `0x4f280` | `0x4d6d0` |

The respective padded handler slots shrink 464 -> 384 -> 352 bytes and
480 -> 368 -> 336 bytes. These are distances to the next registered entry,
including alignment, not exact instruction-body lengths. Native DLL file size
shrinks from 1,425,408 to 1,303,552 bytes (about 8.55%).

## Rejected change: force-inline `SoftTlb::flush_page`

In the retained candidate, a write L2 miss calls `flush_page` at
`0x18004d7af`. That ordinary-ABI cold call causes a `push rbp; sub rsp,30h`
prologue and matching epilogue even when the write hits the micro-TLB.

Forcing the helper inline removes all stack operations on MOV-store's hit path
(41 -> 37 instructions, experimental entry RVA `0x4e840`). But it also changes
L2 indexing/register allocation and duplicates cold invalidation code. The
MOV-store padded slot grows 336 -> 384 bytes and the DLL grows by 23,040 bytes.

Two independent interleaved measurements, with reversed DLL order in the second,
showed an L2 MOV read/write regression of **+1.42%** and **+1.87%** respectively.
The experiment improved stack MOV by about 5.3% and several RMW loops by 2–6%,
but is **not retained** because the L2 regression reproduced. This is a concrete
counterexample to accepting fewer fast-path instructions without measurement.
Its DLLs, assembly, 15-sample and 21-sample data remain in the artifacts.

## Microbenchmark results

Same-process independent DLLs, affinity fixed to the last allowed logical CPU;
15 samples per variant, alternating execution order, two warmups, 750,000 outer
iterations. Each iteration unrolls the named sequence 32 times plus DEC/JNE loop
control. Only `X86_Run` is timed. Pages are allocated and dirtied before timing;
no tracing, hooks, external-page SMC or instrumentation is enabled.

Final registers and both data pages agree across every version. Later inline
comparison runs additionally compare EFLAGS. Original full three-way data:
`micro-three-way-v2.json`; independent repeat: `micro-inline-flush.json`.

| Guest loop | ABI only vs baseline | Retained candidate vs baseline |
| --- | ---: | ---: |
| Generic MOV read/write, L1 | -5.65% | -34.61% |
| Generic MOV read/write, alternating pages / L2 | -11.76% | -32.90% |
| SIB MOV read/write | -5.80% | -34.37% |
| SS stack MOV read/write | -42.44% | -42.19% |
| MOV immediate | -24.63% | -25.25% |
| Register ALU control | -2.75% | -3.47% |
| ADC memory | -25.68% | -26.15% |
| SBB memory | -20.52% | -25.08% |
| RCL memory | -14.47% | -31.88% |
| RCR memory | -11.57% | -30.04% |
| CMP/JNE | -8.89% | -8.84% |

These are **whole guest-loop elapsed-time changes**, not isolated-handler cycle
measurements. For example, MOV-immediate itself retains essentially the same
instruction sequence; its measured loop benefit must not be misrepresented as
25% fewer MOV-handler instructions. Loop control, dispatch, register allocation
and code layout also participate. Likewise these numbers are not predictions of
Webpack speedup. No claim of universal non-regression on untested CPUs/workloads
is made.

The initial microbenchmark attempt stopped at CMP/JNE because its inherited
instruction budget assumed fewer guest instructions. The v2 harness increases
that budget, checks successful completion and reruns **all** 11 loops. The
partial v1 run is preserved but is not used for the table.

## End-to-end Node performance

First exploratory run (parts overlapped builds; not the final stable comparison):

| Case | Mainline | Two-change candidate | Elapsed change |
| --- | ---: | ---: | ---: |
| Webpack serial | 430.586 s | 398.873 s | -7.36% |
| Acorn parse | 60.590 s | 54.585 s | -9.91% |

Both cases exited 0 and their complete result objects equal their native Linux
oracles. The initial runner used an Acorn-only oracle, so it prints `NO_ORACLE`
for Webpack; Webpack was separately checked against the existing native Webpack
oracle. This is not a guest failure and has not been counted as a correctness
pass without that explicit check.

The final independent CLI completed a sequential **ABBA** comparison against the
saved mainline CLI (two fresh samples per variant/case), with a combined oracle
and without concurrent builds, tests, or microbenchmarks:

| Case | Baseline samples | Candidate samples | Baseline median | Candidate median | Elapsed change |
| --- | --- | --- | ---: | ---: | ---: |
| Webpack serial | 427.159, 434.765 s | 377.713, 385.716 s | 430.962 s | 381.715 s | **-11.43%** |
| Acorn parse | 61.047, 60.140 s | 54.559, 54.252 s | 60.594 s | 54.406 s | **-10.21%** |

All eight case executions exited 0 and **PASS**ed full native-oracle result
comparison. Webpack's baseline/candidate sample ranges are 427.159–434.765 /
377.713–385.716 s; Acorn's are 60.140–61.047 / 54.252–54.559 s.
See `node-final-abba.json` and `node-final-abba.log` in the evidence directory.
The exploratory run is excluded from these medians. These are observed local
elapsed-time reductions; n=2 is insufficient for a strong confidence-interval or
universal performance claim. WSL toolchain installation/building was queued
behind this comparison and did not run concurrently with it.

## Correctness and reproduction

- ABI-only: 2,893 selected CPU cases passed.
- ABI plus decoded segment: 2,905 selected CPU cases passed.
- Inline experiment: the same 2,905 cases plus 220 selected managed cases pass.
- Complete quick-suite baseline/inline ON/inline OFF: **3,086 identical
  outcome/message rows** each, 3,080 passing and the same six pre-existing x87
  failures. No skips and no relaxed assertions. `audit-results.json` records the
  explicit differential, including failure messages rather than just counts.
- Final **non-inline** ON/OFF revalidation also has **3,086 identical baseline
  outcome/message rows** per mode (3,080 pass / same six x87 failures), followed
  by **220/220 managed passes**. `validate-final.py` asserts the differential;
  `final-identity.json` records feature bits **1** (fusion ON, profiling OFF),
  default set hash `f5ef132187e8a4f0c69aabd36766ca51417b298b963947c5c7f6fb355c5b2f39`
  and native SHA256. Normal build outputs were restored to fusion ON.

Build/tests (PowerShell, sqlite3's directory also required for managed tests):

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
$env:PATH = 'C:/Users/Jiang/miniconda3/Library/bin;' + $env:PATH
dotnet build Podish.Cli/Podish.Cli.csproj -c Release -p:EnableSuperOpcodes=true
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --filter 'Category!=Exhaustive'
# Run the same quick filter with -p:EnableSuperOpcodes=false; compare to mainline.
```

Local evidence scripts, from the repository root:

```powershell
python artifacts/handler-perf-20771da/validate-final.py
python artifacts/handler-perf-20771da/inspect-handlers.py artifacts/handler-perf-20771da/cli-final/fibercpu.dll artifacts/handler-perf-20771da/asm-final
python artifacts/handler-perf-20771da/repeat-node.py --candidate cli-final --tag final
```

Do not overwrite candidate DLLs or run the microbenchmark/build concurrently
with the Node comparison. The large workload images and raw artifacts are
local, ignored evidence, not newly checked-in dependencies.

## Remaining costs / next experiments

The subsequent [WSL/Linux codegen verification](handler-codegen-wsl-20261117.md)
built the complete CLI on ext4 with Linux Clang 22.1.8 and reproduced the same
probe issue. It records three linked ELF variants, 52 -> 44 -> 33 generic-load
and 61 -> 49 -> 38 generic-store L1-hit instructions, and an exact 3,086-row
baseline/final quick-suite differential. These are codegen/correctness results,
not a Linux end-to-end speedup measurement.

- At this measurement checkpoint, write-handler stack setup remained because of
  the cold ordinary-ABI call. The subsequent tail-defer application removes it
  without blindly force-inlining the helper; its incremental performance
  comparison is separate from the ABI/segment measurements above.
- Generic EA execution still extracts base/index/scale and handles address-size
  truncation; the common existing MOV specializations already avoid some of it.
- MOV-immediate/Group-1 wrappers still check immutable has-immediate/operand-size
  metadata. Eliminating these checks requires an explicit valid-opcode/decode
  contract or additional specialization, not a blanket removal of checks.
- Cross-page, fault, write-permission, SMC retry and live segment-base operations
  are correctness requirements, not removable redundancy.
- A pinned-core loop result does not measure front-end/uop behavior directly;
  no hardware-counter claim is made. Other architectures/toolchains have not
  received performance validation in this Windows x64 experiment.
