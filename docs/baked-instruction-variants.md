# Baked instruction variants

CMake generates native scalar handlers for the interpreter's 16-byte slots from the
[ModRM table](../libfibercpu/presets/3.5m/modrm.json) and
[scalar plan](../libfibercpu/presets/3.5m/scalar.json) in the default `3.5m`
preset. The [native build presets](build-presets.md) describe the other table sets.
The scalar plan binds prefixes, flag liveness, effective addresses and immediates
for selected shapes. The C++ and exact LLVM paths use the catalog kernel
bindings. You can build and run the C++ handlers without LLVM.

The decoder tries exact scalar, partial scalar, and ModRM bindings in that
order. A ModRM entry can also bind width and flags. For some memory operands,
the decoder masks the ModRM bits that the kernel gets from the decoded
effective address. If no baked entry matches, the decoder selects a scalar
binding for the slot count. The
architectural catalog selects mandatory-prefix variants such as MMX or SSE.

In a version 4 scalar entry, `slots` fixes the decoded slot count to 1 or 2.
Set `disp` or `imm` to `null` in its operand record to read that value from the
runtime op. The lookup still matches the other fields and the slot count. It
tries single-field variants before variants with both fields dynamic. Exact
entries retain the version 3 format. Fusion plans still use exact shapes.

Fusions also come from recorded profiles. The checked-in sequence manifest
contains canonical instruction IDs. The decoder selects a fusion when all of
its operand shapes match an entry in the baked plan. Other sequences use their
scalar handlers. The LLVM JIT re-decodes guest bytes with architectural handlers
to build exact block recipes. See [the JIT options](instruction-specialization.txt).

## Train the baking presets

Run the [preset trainer](../scripts/train_nested_presets.py) with Python 3.11
or later on a macOS ARM64 calibration host. It captures the configured workloads with static
specialization, SuperOpcodes, online JIT and linked block caching disabled.
It ranks observed ModRM, width/flags, exact and partial scalar, and fusion
shapes. Partial candidates group observed operands after removing the
displacement, the immediate, or both from the match key.

Each round adjusts how much of each ranked list to include. The trainer
learns the code size cost from measured builds and verifies each candidate's
guest output. It ranks candidates by the geometric mean of training-case
speedups against a build without baking. It remeasures the finalists and
freezes three strict supersets against the 1.75, 3.5, and 10 MiB native text
budgets. The size check during training includes the generic interpreter.

From the repository root, configure the training target with a workload JSON.

```sh
cmake -S libfibercpu -B out/train -DCMAKE_BUILD_TYPE=Release \
  -DFIBERCPU_PRESET_TRAINING_CONFIG=/absolute/path/to/training.json
cmake --build out/train --config Release --target fibercpu_train_presets
```

For a different round count or a comparison without partial scalar forms,
invoke the same script directly with `--rounds` or `--no-partial-operands`.

```sh
python3 scripts/train_nested_presets.py --config /path/to/training.json \
  --output out/preset-training --rounds 10
```

The config names a writable IA-32 guest `rootfs`, the matching
framework-dependent Podish build directory in `managed_cli`, the path to
`Podish.PerfTools.dll` in `perf_tools`, the `cmake` executable, its
`cmake_args`, and a `benchmark_lock` shared with other local benchmarks.
Each entry in `workloads` supplies `id`, `group`, guest `cwd`, `shell`,
`repetitions` and the expected output `sha256`. Optional `verify` selects
the output representation to hash. Use a fresh output directory for each
new suite.

The CMake target writes captures, plans, and timings under
`out/train/preset-training/Release/`. The `frozen/` directory contains
the selected plans, native libraries, baseline, and provenance. Compare the
frozen libraries and baseline on a separate holdout suite with the same
guest inputs and runtime settings. After review, copy the four JSON files
and, for tiers with fusion, the generated C++ source into the matching
`libfibercpu/presets/<tier>/` directories. A normal CMake build reads those
files directly.

Use `FIBERCPU_BAKING_PRESET=custom` and the individual plan options to try
candidate tables. Set `FIBERCPU_MODRM_PLAN_FILE` for the ModRM plan,
`FIBERCPU_STATIC_OPERAND_PLAN_FILE` for the scalar plan, and
`FIBERCPU_GENERATED_SUPEROPCODES_FILE` and `FIBERCPU_OPERAND_PLAN_FILE` for
the paired fusion source and operand shapes. An empty scalar plan path
leaves the ModRM bindings enabled.

Relative control transfers share an operand shape across encoded offsets.
The decoder resolves the target into the runtime slot and retains the
encoding for profile export. Data immediates and the stack adjustment of
`RET imm16` still distinguish shapes. Recapture profiles after changing
canonical instruction identities or the decoded operand representation.

## Code generation review

Inspect the emitted machine code as part of a table update.

| Path | Expected generated code |
| --- | --- |
| Baked scalar | An exact scalar shape passes fixed prefixes, effective address and immediate values to the shared kernel. The fixed ModRM operand reaches the kernel as a literal. Register selection and the ModRM register-versus-memory decision disappear when the kernel uses them. The instruction advances one or two slots with a fixed displacement. A ModRM-only key leaves other operands dynamic. A matching scalar policy additionally fixes the width and omits updates to dead flags where specified. |
| Baked fusion | The exact operand records supply the ModRM, immediates and slot count of every constituent. The final handler performs one tail dispatch. Each memory operation retains its restart/retry exit. |
| LLVM exact | The block compiler inlines the operation templates with their decoded operands before running the LLVM optimization pipeline. On the ordinary path, a baked memory immediate should appear as an instruction constant and the PC for replay should be a fixed offset from the original slot pointer. Branch targets and links still come from block runtime storage. |

On ARM64, use `llvm-nm` and `llvm-objdump -d` on libfibercpu to inspect
`DispatchWrapper` instantiations with `WithModRM` and `SlotBound` in the
signature. Include a register form and a two-slot memory-immediate form.
Use `JitMode=profile` and `WeightedProfilePath` to capture the LLVM probe recipe.
`fibercpu_llvm_cache --linked PROFILE BUNDLE --llvm-opt-level O2` emits objects
with the online compiler's exact-operand preparation. Run
[extract_linked_block_cache.py](../scripts/extract_linked_block_cache.py) with
the bundle and the producer ABI fingerprint, then disassemble the matching
object. Run the probe with `JitMode=exact` to check execution and replay.

Use `-XX:LlvmFoldOperandLoads=on` for the extra post-inline folding pass in
the online exact compiler. To compare linked-cache objects from the same
profile, run `fibercpu_llvm_cache` with `FIBERCPU_LLVM_FOLD_OPERAND_LOADS=1`,
then with `FIBERCPU_LLVM_FOLD_OPERAND_LOADS=0` and a different output path.

The initial ARM64 review, recorded on 2026-11-20, counted 1,258 baked scalar symbols, including 17
two-slot bindings. The inspected Group 1 register add advanced 16 bytes. The
two-slot memory compare loaded its immediate from offset 24 and advanced 32
bytes. Both selected the ModRM operation at compile time. The LLVM exact
probe materialized `0x12345678` with `mov` and `movk`, inlined the instruction
bodies and retained the MMU replay and block-link exits.
