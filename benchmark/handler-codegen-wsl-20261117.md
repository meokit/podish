# WSL/Linux handler codegen verification

## Build and scope

The complete Release CLI was built successfully **inside WSL's ext4 filesystem**:

```text
/root/podish-codegen-20771da-20261117-6n9doi/source
```

`findmnt` identifies the build filesystem as `/dev/sdc`, ext4, not `/mnt/c`.
Only source archives, orchestration scripts and exported evidence cross the
Windows mount. Windows `bin`, `obj`, native build trees, caches and workload
artifacts were not copied into the Linux source tree.

Environment:

- Ubuntu 24.04, WSL2, Linux x86-64.
- Linux Clang **22.1.8**, libc++ 22, CMake 3.28.3.
- .NET SDK **10.0.115**, runtime **10.0.15**; Cargo 1.100.0.
- Actual native compile options include `-O3 -DNDEBUG -std=c++23 -flto=thin
  -fPIC -fomit-frame-pointer -fno-stack-protector -march=native
  -stdlib=libc++ -fno-strict-aliasing -fno-rtti -fno-exceptions`.
- Three independently linked ELF shared objects: clean `20771da`, ABI-probe fix
  only (including the required `ChainToKnownBlock` annotation), and the retained
  ABI-plus-decoded-segment candidate. No force-inline TLB experiment is included.
- All three export feature bits **1** and the same default superopcode hash
  `f5ef132187e8a4f0c69aabd36766ca51417b298b963947c5c7f6fb355c5b2f39`.

The CLI build had zero errors and existing x87 compound-literal warnings.
WSL initially had an interrupted/corrupt `linux-libc-dev` package installation;
that package was reinstalled before installing the build tools. This was an
environment issue, not a project source/build fix.

Raw evidence is exported to `artifacts/wsl-codegen-20771da/`: source archive
identities, full build logs, compiler commands, three shared objects, handler
registries, `llvm-nm` symbol sizes, disassembly and test reports. Original Linux
build trees and evidence remain under the directory above its `source` child.

## The probe issue reproduces with the real Linux compiler

```text
__has_cpp_attribute(preserve_none)        = 0
__has_cpp_attribute(clang::preserve_none) = 1
__has_attribute(preserve_none)            = 1
```

Therefore the old unqualified C++ probe silently selected the regular **SysV**
ABI on this Linux compiler too. This is not a Windows-only detection problem.
GNU spelling is supported; the namespaced Clang C++ spelling is also supported.
These Linux x86-64 results do not establish what an older ARM compiler emitted.

## Actual MOV L1-hit paths

Addresses below are ELF virtual addresses relative to the shared object's load
base. They were obtained from the running library's handler-pointer registry,
then disassembled from the **linked shared object**, not an isolated surrogate.
Counts include the conditional miss check (not taken) and indirect tail jump;
they are instruction counts, not uops, cycles, or measured speedups.

| Wrapper | Baseline | ABI only | ABI + decoded segment |
| --- | ---: | ---: | ---: |
| Generic MOV-load ECX instructions | **52** | **44** | **33** |
| Generic MOV-load push/pop instructions | 6 | 0 | 0 |
| Generic MOV-load explicit RSP references | 2 | 0 | 0 |
| Generic MOV-store EAX instructions | **61** | **49** | **38** |
| Generic MOV-store push/pop instructions | 13 | 2 | 2 |
| Generic MOV-store explicit RSP references, including adjustments | 3 | 2 | 2 |
| Specialized ESP-base MOV-load instructions | 29 | 21 | 21 |

The store's baseline 13 push/pop instructions are six register-save pairs plus
an extra `push rax` used as stack allocation; the latter is discarded with
`add rsp,8`, not a matching pop.

### 1. ABI redundancy is present, but smaller than on Windows

Baseline MOV-load starts at `0x97ed0`:

```asm
push rbp
push r14
push rbx
; ...
mov  rax, [rsp+20h]      ; seventh threaded argument: flags cache
; ... memory access ...
mov  [rsp+20h], rax
pop  rbx
pop  r14
pop  rbp
jmp  r11
```

ABI-only entry `0x95b00` and final entry `0x942c0` have **no push/pop and no RSP
reference on this path**. State/op are threaded in `r12/r13`, rather than the
ordinary SysV `rdi/rsi`. The epilogue is simply:

```asm
mov [r12+4], eax
mov rax, [r13+20h]
add r13, 20h
jmp rax
```

Ordinary SysV passes six integer/pointer arguments in registers, versus four
under the Windows x64 ABI. Consequently this baseline spills/forwards only one
threaded argument on the stack, not Windows' three. The actual load-save count
is three pairs here versus four on Windows. Both are eliminated by the fix.

### 2. Segment predecode also removes 11 instructions here

The ABI-only generic load still performs the full base/override DS/SS selection
around `0x95b39`, with a `mov r8b,4` scheduled earlier at `0x95b29`.
The final version replaces that selection with:

```asm
; 0x942f6
mov   ebx, 0311h
bextr eax, eax, ebx
mov   r8d, [r12+rax*4+34h]
```

`0x0311` encodes start bit 17, length 3: exactly the decoded resolved-segment
field. The live segment-base load remains. The generic load path drops from
44 to 33 instructions; generic store drops from 49 to 38. The existing
ESP-specialized load already knows its segment, so this second change does not
further reduce that handler's 21-instruction path.

### 3. The cold-call stack-frame issue is not Windows-only either

The final Linux store still starts with:

```asm
; 0x91a40
push rbp
sub  rsp, 10h
```

And even its L1-hit dispatch runs:

```asm
; 0x91ab7
mov rax, [r13+20h]
add r13, 20h
add rsp, 10h
pop rbp
jmp rax
```

The cold write-miss path calls
`fiberish::mem::SoftTlb::flush_page(unsigned int)` at **`0x91b1e`**, targeting
`0x59610`. It spills `esi` and `rdx` around that regular-ABI call. Thus the
ordinary call still influences register allocation/frame setup for the hot
leaf-like path. Linux reserves 16 bytes here, not Windows' 48-byte allocation.
The earlier force-inline experiment is still rejected on its reproduced
Windows L2 regression; this WSL task did not silently reintroduce it.

## Other codegen observations / remaining opportunities

1. **Native target tuning matters.** Linux's existing CMake configuration uses
   `-march=native`, and EA calculation uses `bextr` and memory-source `shlx`.
   For example, index extraction uses control `0x0606` (start 6, length 6).
   Address+3 is folded into an LEA displacement. Windows' build uses a different
   compiler/target configuration and does not emit the same sequences. Its
   63 -> 36 load count must not be compared to Linux's 52 -> 33 as a pure OS or
   ABI experiment; the three Linux variants themselves do use matching flags.
2. **Fewer hot instructions does not ensure a smaller full function.** Exact
   ELF symbol sizes for MOV-load ECX are **398 -> 400 -> 374 bytes**. ABI-only
   Clang duplicates tail-dispatch finishing sequences on multiple cold paths,
   even though it removes the hot saves. MOV-store sizes are 410 -> 352 -> 327.
   These are actual `llvm-nm` symbol sizes, unlike Windows' previously reported
   padded distances between registered handlers.
3. **MOV-immediate retains invariant metadata checks.** At final `0x8af50`,
   `GetImm` checks `has_imm`, although valid B8..BF opcodes always have an
   immediate. The ordinary 32-bit path takes `jne 0x8af76` to load the immediate,
   then `je 0x8af68` back to the common store/dispatch. The zero default and the
   operand-size check also remain. A validated 32-bit specialization could
   remove this work; it was observed, not changed or benchmarked here.
4. **Already-small controls remain small.** The no-flags register ADD wrapper
   is 35 -> 36 -> 36 bytes and still checks the immutable has-immediate bit.
   The flags-producing register ADD shrinks 168 -> 140 -> 140 bytes; the fused
   CMP/JNE wrapper shrinks 299 -> 241 -> 241 bytes.
5. **Internal slow transitions may go through ELF PLT entries.** The final
   load's slow memory restart ends with a jump to `MemoryOpGeneric<true>@plt`.
   Its normal successful dispatch is still a register-indirect tail jump, not
   a normal call/return pair. No PLT-visibility optimization was attempted.

Library file sizes are 1,845,688 -> 1,771,968 -> 1,743,112 bytes (about 5.56%
smaller for the final candidate). This is footprint evidence, **not a Linux
runtime performance measurement**; no WSL Webpack speedup is claimed.

## Correctness and reproduction

- Full Release `Podish.Cli` build succeeded, including Rust netstack and native
  CPU library; no Linux product-source workaround was needed.
- The selected regression/integer/addressing/isolation/SIMD suite passed
  **2,905/2,905**, including the new cached-code/live-segment-base tests.
- Full quick-suite baseline/final differential: **3,086 identical outcome/message
  rows**, **3,080 passed**, the same six pre-existing x87 failures, zero skips
  and no differences. CLI `--help` also exited successfully. Evidence is under
  `tests/` and `logs/`; the test output was restored to the final native library.

Run inside WSL as the user owning the directory (root for this copy):

```bash
export PATH=/usr/lib/llvm-22/bin:/root/.cargo/bin:$PATH
export CC=clang CXX=clang++
export CMAKE_BUILD_PARALLEL_LEVEL=8
export CPM_SOURCE_CACHE=/root/.cache/podish-cpm
cd /root/podish-codegen-20771da-20261117-6n9doi/source
dotnet build Podish.Cli/Podish.Cli.csproj -c Release
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release \
  -p:FiberishSkipNativeBuild=true \
  --filter 'FullyQualifiedName~Regression|FullyQualifiedName~Integer|FullyQualifiedName~Addressing|FullyQualifiedName~Isolation|FullyQualifiedName~Simd'
```

Native shared objects are saved as `../binaries/libfibercpu-{baseline,abi-only,final}.so`.
Disassembly is under `../assembly/{baseline,abi-only,final}/`; each includes
`handlers.json`, `summary.json`, `symbols-nm.txt` and actual compile commands.
The exported Windows-side copies use the same relative layout under
`artifacts/wsl-codegen-20771da/`. These are frozen evidence for the pre-tail-defer
codegen checkpoint; the live WSL source/build subsequently received tail-defer.

Follow-up: [preserve-all versus tail-dispatched `flush_page`](flush-page-codegen-wsl-20261117.md)
tests both approaches in independent WSL copies. Both remove the ordinary
store's hit-path frame; the tail-defer version also removes the store wrapper's
cold ordinary call. Tail-defer was subsequently applied to both working copies;
preserve-all remains an isolated experiment. See that follow-up's application
checkpoint for the newer validation and still-running performance comparison.
