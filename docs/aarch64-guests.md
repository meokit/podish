# Linux AArch64 guests

Podish selects i386 or little-endian AArch64 from an executable's ELF class and machine. The same CLI can run both, including an `execve` that changes architecture. The ELF interpreter must match the executable. To pull an AArch64 image, pass `--platform linux/arm64` to `pull`. Guest processes use the shared VFS, scheduler and syscall services.

## ISA and ABI scope

The interpreter targets Armv8-A A64 user mode. It implements scalar integer and branch instructions, common register, pair and structure transfers, exclusive and selected LSE atomics, user-mode system registers, scalar floating point, and a subset of Advanced SIMD. CRC instructions and selected extensions are individually implemented. Half-precision format conversion supports the IEEE and Arm alternative formats. FP16 arithmetic, SVE, SME, privileged execution, and full conformance to an Arm architecture version are outside current coverage. AArch64 ELF auxiliary vectors advertise zero for HWCAP and HWCAP2.

The register file and Linux argument and return values are 64-bit. The shared MMU and VMA allocator currently support addresses below 4 GiB. The loader rejects segments outside that range and memory transfers reject high or wrapping pointers. AArch64 dynamically linked programs require an AArch64 userspace in their root filesystem.

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

The threaded calling convention passes a CPU pointer, a slot pointer, the remaining instruction budget after the block, a read-TLB pointer and the guest NZCV bits. A build can add one guest GPR as the last argument with `-DFIBERCPU_AARCH64_PIN_REG=9` or `-p:AArch64PinRegister=9`. The default is `-1` and emits the shorter function type. The runtime frame belongs to the current invocation of `Run`. Nested calls restore the outer frame on return.

The decoder scans each block backward for NZCV reads and writes. All four flags are live on block exit and at instructions that can fault or call an observer. When a later writer kills every output of an integer flag producer, lowering selects a handler that omits that write. Carry-consuming arithmetic still reads the incoming C. The word cache retains the precise handler for single steps and short budgets. Use `-DFIBERCPU_AARCH64_DFE=OFF` for a control build.

The AArch64 cache holds decoded straight-line spans on one executable page. Instruction budgets, synchronous faults, syscalls and self-modifying code can all interrupt a span. Slow memory callbacks force instruction refetch. Threaded dispatch uses the same compiler attribute probe as x86. Compilers without guaranteed tail calls use a bounded loop. See [baked instruction variants and training](baked-instruction-variants.md) for profiling and regeneration.

Within a full block, the slot identifies the precise guest PC of every constituent, including the interior of a fused sequence. Block binding resolves ADR, ADRP, literal addresses and immediate branch targets after copying the word-cache entry. The remaining budget passes unchanged through ALU handlers. An early exit refunds the unretired suffix. Same-page links bind to a host executable page and check the target slot generation and current instruction bytes. The taken immediate branch uses a shared tail-called entry. Ordinary block chaining carries NZCV and the optional pin directly.

A hot load checks the address range, full page tag and page boundary using the ABI's TLB argument. A miss tail-calls the scalar replay entry with the offending slot, before changing the destination or applying writeback. Replay publishes the current PC, NZCV and pin before a possible observer. A callback-free completion can tail-dispatch to the next member. An observing callback forces a refetch. Guest stores currently use this synchronized path, which checks for executable aliases, dirty-page tracking and mapping hooks.

The AArch64 backend provides lowered, offline-baked and fused interpreter handlers. The online LLVM block compiler continues to target i386.

## Linked handler code generation

Apple Clang 21 on an Apple M5 Max assigns `x20` to CPU state, `x21` to the slot, `x22` to the budget, `x23` to the read TLB, and `w24` to NZCV. In the optional pin build, `x25` holds the selected guest GPR. These assignments describe the inspected Release binary. The ABI is expressed using typed arguments and `preserve_none` so other host compilers can allocate their own registers.

The table counts static host instructions in linked entries, including dispatch. The probe used `FIBERCPU_AARCH64_PIN_REG=9` and exact operand entries for the listed guest words. The read example includes both the hit path and its direct jump to replay. The conditional branch calls the shared target-validation entry with a tail jump, so its count excludes that shared code.

| Guest encoding | Entry | ARM64 host instructions |
| --- | --- | ---: |
| `91001529` | `ADD X9,X9,#5` | 3 |
| `BA020129` | `ADCS X9,X9,X2` | 6 |
| `54000042` | `B.HS +8` | 4 |
| `F9400429` | `LDR X9,[X1,#8]` | 17 |
| `BA020129 54000042` | Fused `ADCS` and `B.HS` | 9 |
| `91001529 F940012A 71000400 54FFFFE1` | Fused add, load, subtract and branch | 27 |

A separate scan of the stock AArch64-only Release library with the pin disabled covered all 441 baked entries, 201 integer entries and 78 memory entries. None contained a normal call or a stack operand on this host. The shared branch and end-of-block entries also stayed stackless. The scalar fallback and memory replay entries call the platform ABI after publishing guest state.

The complete entry for `ADD X9,X9,#5` is shown below.

```asm
ldr x0, [x21, #0x48]!
add x25, x25, #5
br  x0
```

The stock i386 guest's baked `ADD EAX,0x17eaff` on the same host advances its 16-byte slot and takes two additions for its immediate, then tail-jumps through the loaded address. Both entries keep the guest operand in a register throughout. The A64 fused load's replay exit advances the slot by 72 bytes to the load, then jumps directly to `ReplayMemory`; the preceding add has already committed to the hot pin.

To reproduce a probe, add its numeric words to an AArch64 version-1 plan (`entries`, each with `word` and `count`). Provide `pairs` or `sequences` to emit fused entries. Configure `FIBERCPU_AARCH64_OPERAND_PLAN_FILE` with this plan, build Release and resolve `BakedThreadRun`, `ChainDirect`, and `ReplayMemory` with `nm -n -C libfibercpu.dylib`. Pass the symbol's start and the next symbol's address to `xcrun llvm-objdump -d --no-show-raw-insn --start-address=... --stop-address=... libfibercpu.dylib`.

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

AArch64 reached 99.0% of i386 throughput in this workload. All six runs passed CoreMark's time check and matched the four CRCs (`e714`, `1fd7`, `8e3a`, `bd59`). CoreMark is also one of the inputs to the stock AArch64 training profile.
