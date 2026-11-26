# Linux AArch64 guests

Podish selects i386 or little-endian AArch64 from an executable's ELF class and machine. The same CLI can run both, including an `execve` that changes architecture. The ELF interpreter must match the executable. To pull an AArch64 image, pass `--platform linux/arm64` to `pull`. Guest processes use the shared VFS, scheduler and syscall services.

## ISA and ABI scope

The interpreter targets Armv8-A A64 user mode. It implements scalar integer and branch instructions, common register, pair and structure transfers, exclusive and selected LSE atomics, user-mode system registers, scalar floating point, and a subset of Advanced SIMD. CRC instructions and selected extensions are individually implemented. Half-precision format conversion supports the IEEE and Arm alternative formats. FP16 arithmetic, SVE, SME, privileged execution, and full conformance to an Arm architecture version are outside current coverage. AArch64 ELF auxiliary vectors advertise zero for HWCAP and HWCAP2.

The register file, virtual addresses, and Linux argument and return values are 64-bit. AArch64 uses the lower 39-bit user range, up to `0x8000000000` exclusive. The native MMU allocates three levels of 512 entries on demand for 4 KiB guest pages. The loader and the managed VMA allocator check the full range before mapping or accessing memory. AArch64 dynamically linked programs require an AArch64 userspace in their root filesystem.

The loader keeps the existing low-address stack, PIE, interpreter and vDSO placements. A fixed mapping or ELF segment can use the full range, and address selection can fall back above 4 GiB when the low window has no suitable gap. A `PROT_NONE` reservation creates a VMA without allocating the corresponding page tables or backing pages.

Instruction encodings and behavior follow the per-instruction encoding diagrams, constraints and Operation pseudocode in the [A64 ISA](https://developer.arm.com/documentation/ddi0602/latest/) and the [Arm Architecture Reference Manual](https://developer.arm.com/documentation/ddi0487/latest/). Linux userspace layouts follow the [ELF64 ABI](https://github.com/ARM-software/abi-aa/blob/main/aaelf64/aaelf64.rst), the [Linux syscall table](https://github.com/torvalds/linux/blob/master/scripts/syscall.tbl) and the [Linux arm64 signal implementation](https://github.com/torvalds/linux/blob/master/arch/arm64/kernel/signal.c).

## Build and run

With .NET 11 and the repository's standard native toolchain installed, a normal Release build includes both guests.

```sh
dotnet build Podish.Cli -c Release
dotnet Podish.Cli/bin/Release/net11.0/Podish.Cli.dll run --rm --rootfs /path/to/arm64-rootfs /bin/sh
dotnet build Podish.Cli -c Release -p:FiberGuestI386=false
dotnet build Podish.Cli -c Release -p:FiberGuestAArch64=false
```

Standalone CMake builds use `FIBERCPU_GUEST_I386` and `FIBERCPU_GUEST_AARCH64`. Enable at least one. In a single-guest managed build the selected architecture is a compilation constant and syscall dispatch uses the concrete CPU type. ELF validation still checks incoming binaries. WebAssembly uses Berkeley SoftFloat. Native hosts can exercise the same arithmetic path with `-DFIBERCPU_AARCH64_SOFTFLOAT=ON`.

## Interpreter and specialization

The fixed-width decoder indexes 256 buckets with bits 31–24, tests the candidate encoding masks, checks reserved operands and extracts fields. Lowering binds the operation to a handler. Integer and memory variants specialize common widths, operations, shift and addressing modes. Scalar FP and SIMD lowering classify their instruction families before execution. Hot operand and fusion plans use the very same integer and memory kernels. The x86 decoder retains the opcode maps, prefixes, and ModRM tables its variable-length encoding requires.

The decoder uses `DecodedInstruction` during lowering and writes `DecodedOp` records into a `BasicBlock`. A `ScalarHandlerFunc` implements the scalar fallback.

The `HandlerFunc` calling convention passes CPU state, the current `DecodedOp`, the remaining budget after the block, the software TLB, the micro-TLB tags and addend, and NZCV. A build can add one guest GPR as the last argument with `-DFIBERCPU_AARCH64_PIN_REG=9` or `-p:AArch64PinRegister=9`. The default is `-1` and emits the shorter function type. Nested calls restore the outer run frame on return.

Built-in instructions bind directly to a `HandlerFunc`. Register-only SIMD, scalar FP and system handlers use the dispatch copies of NZCV and the optional pinned register. On little-endian arm64 hosts, byte-vector pairwise min/max and equality use NEON. On little-endian non-ARM hosts, those handlers and the integer arithmetic, shift, reduction, permutation and table lookup kernels use SIMDe. The interpreter masks inactive lanes and updates guest FPSR.QC for saturating arithmetic. Floating-point operations retain their guest FPCR and FPSR handling through the host floating-point environment or SoftFloat. SIMD structure loads and stores use the interpreter's precise memory paths. `RunScalarHandler` adapts scalar-only bindings supplied by a profiling client.

The decoder scans each block backward for NZCV reads and writes. All four flags are live on block exit and at instructions that can fault or call an observer. When a later writer kills every output of an integer flag producer, lowering selects a handler that omits that write. Carry-consuming arithmetic still reads the incoming C. The word cache retains the precise handler for single steps and short budgets. Use `-DFIBERCPU_AARCH64_DFE=OFF` for a control build.

AArch64 uses the same block arena and backing-page invalidation policy as i386. Its block index keys executable host backing and the full guest PC. A 128-entry lookup cache precedes the page index. Both guests charge actual decoded block allocation bytes against `BlockCacheSize`, defaulting to 64 MiB per address space. Budget pressure exits the interpreter before releasing the arena. The next native run performs the pending flush. One instruction can retire through the scalar path when a block cannot fit.

The decoder emits a 16-byte `DecodedOp` for compact operands and a 32-byte slot for wide operands and bound addresses. Each slot carries its guest instruction ordinal. The interpreter recovers the PC from the block start and that ordinal, including at the interior of a fused sequence. Non-entry architectural NOPs are folded out of the slot stream. The block footer keeps the original words and any scalar fusion bindings. The guest budget still counts folded NOPs. A bounded run can stop at a folded NOP, and a branch directly to its PC starts a new block. Block binding precomputes ADR, ADRP, literal and immediate branch addresses. ALU handlers carry the remaining budget unchanged and an early exit refunds the unretired suffix. Each block has distinct taken and fallthrough links. A link loads the target entry and metadata together, validates the target PC and block validity, charges the budget, and tail-dispatches across executable pages. A cold lookup either installs the link and tail-dispatches or returns control to the decoder. A budget shorter than the target block uses bounded dispatch.

The managed runtime invalidates cached targets after host-side code changes. For example, `VMAManager.NotifyFileContentChanged` sends `ResetCodeCacheByRange` to the affected engines. Native remaps, removal of execute permission and guest writes through a writable alias invalidate blocks through the backing-page index. A stale link then fails its target validity check. The standalone callback interface, which has no managed invalidation protocol, keeps a per-run cache and checks bytes before reusing a block.

Loads, stores, SIMD structure transfers, exclusive and atomic instructions, and DC ZVA use the x86 two-level data TLB policy. Atomic and exclusive handlers check the required permissions before updating guest state, so a miss can replay the current instruction through the MMU callbacks. A hit in the register micro-TLB or a software read or write table performs the transfer inline. Each software data table defaults to 1024 entries and uses the `FIBERCPU_DATA_TLB_ENTRIES` CMake setting. An L2 hit promotes both tags and the addend into the handler ABI. The write table only holds dirty pages that require no write interception. The fast probe checks the entire page tag, address range and transfer width. A miss tail-calls replay at the current instruction before updating a register or guest memory. Replay publishes PC, NZCV and the optional pin before a possible callback. A callback-free replay resumes the block. A transfer through the MMU slow path refetches the next instruction.

The compiler attribute probe for tail dispatch is shared with x86. Compilers without guaranteed tail calls use bounded dispatch. LLVM block compilation targets i386. See [baked instruction variants and training](baked-instruction-variants.md) for profiling and regeneration.

## Linked handler code generation

On Apple Clang 21, the Release AArch64 handler ABI uses `x20` for CPU state, `x21` for the slot, `x22` for the budget, `x23` for the software TLB, `x24` and `x25` for the micro-TLB tags and addend, and `w26` for NZCV. The optional guest GPR occupies the last ABI argument. The inspected branch hit uses one link load, a paired `ldp` of the target header, validity and budget checks, and `br` through the loaded entry. The inspected 32-bit load and store handlers perform the access and tail-dispatch on a hit. Branch misses use a direct tail branch to `ResolveBranchTargetSlow`, and transfer misses use a direct tail branch to `MemoryOpGeneric`. The inspected hot entries have no stack frame.

To reproduce a probe, add an instruction's numeric encoding to an AArch64 version-1 plan (`entries`, each with `word` and `count`). Provide `pairs` or `sequences` to emit fused entries. Configure `FIBERCPU_AARCH64_OPERAND_PLAN_FILE` with this plan, build Release and resolve `BakedHandler`, `LoadStoreHandler`, `ResolveDirectBranchTarget`, and `MemoryOpGeneric` with `nm -n -C libfibercpu.dylib`. Pass the symbol's start and the next symbol's address to `xcrun llvm-objdump -d --no-show-raw-insn --start-address=... --stop-address=... libfibercpu.dylib`.

## Cache and TLB counters

Build with `-DENABLE_TLB_STATS=ON` and export the workload process's counters with `--guest-stats-dir`. Set `PODISH_SKIP_BLOCKS_EXPORT=1` for a counters-only run and `exec` the measured program when a shell prepares input and output. The `native_stats` field in `summary.json` contains direct-link hits, slow link resolutions, cross-page hits, link misses, dispatcher lookups, block allocations and bytes, memory replays, and individual L1/L2 TLB probe totals. Equal cache limits apply to allocation bytes. Code shape, block length and guest placement can change the number of blocks and TLB conflicts under that limit.

## Reproduce validation

```sh
cmake -S libfibercpu -B /tmp/fibercpu-a64 -DCMAKE_BUILD_TYPE=Release -DFIBERCPU_TEST_AARCH64=ON
cmake --build /tmp/fibercpu-a64
ctest --test-dir /tmp/fibercpu-a64 --output-on-failure
tests/aarch64/run-native-differential.sh
tests/aarch64/run.sh dotnet "$PWD/Podish.Cli/bin/Release/net11.0/Podish.Cli.dll"
tests/aarch64/run-qemu-tcg.sh /path/to/qemu-v10.1.0 dotnet "$PWD/Podish.Cli/bin/Release/net11.0/Podish.Cli.dll"
dotnet test Fiberish.Tests -c Release
dotnet test Fiberish.X86.Tests -c Release
```

The native differential runner requires an AArch64 host. It assembles the corpus, seeds architectural inputs reproducibly, and compares real hardware with the interpreter, including the exact baked path for eligible instructions. Reserved encodings, faults, SMC, cache behavior, budget boundaries and callback reentry have separate probes. The ELF fixtures exercise static, PIE, PT_INTERP and cross-architecture exec. The QEMU helper compiles selected upstream tests and compares the same Linux ELF's output and exit status with native arm64. It requires Zig and access to the native Linux oracle described in its usage. These sampled tests leave untested encodings and state combinations. The managed suites exercise the Linux signal frame and device layouts as well as existing i386 behavior.

For a reproducible guest-to-guest benchmark, compile both CoreMark binaries from the same EEMBC checkout and run them alternately through one Release CLI. Use enough iterations for both guests to satisfy CoreMark's ten-second validation rule.

```sh
python3 benchmark/compare_guest_coremark.py --coremark /path/to/coremark --cli Podish.Cli/bin/Release/net11.0/Podish.Cli.dll --iterations 60000 --repetitions 3
```

On an Apple M5 Max with macOS 27.2.1, the dual-guest Release CLI built from commit `27d317aa` used the stock 3.5m preset, LTO, and no AArch64 GPR pin. Zig 0.15.2 compiled both musl guest binaries at `-O2` from EEMBC CoreMark commit `1f483d5b8316753a742cbf5590caf5bd0a4e4777`. At 60,000 iterations, the three alternating measurements produced the following results.

| Guest | Iterations per second | Median |
| --- | --- | ---: |
| i386 | 3556.2, 3542.5, 3557.7 | 3556.2 |
| AArch64 | 3515.1, 3560.0, 3522.4 | 3522.4 |

AArch64 reached 99.0% of i386 throughput in this workload. All six runs passed CoreMark's time check and matched the four CRCs (`e714`, `1fd7`, `8e3a`, `bd59`).
