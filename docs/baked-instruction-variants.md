# Baked instruction variants

CMake generates native scalar handlers for the interpreter's 16-byte slots from the
[ModRM table](../libfibercpu/src/generated/modrm-specializations.json) and the
[exact operand plan](../benchmark/optimization/manifests/baked-scalar-operands-v3.json).
The exact plan binds prefixes, flag liveness, effective addresses and immediates
for selected shapes. The C++ and exact LLVM paths use the catalog kernel
bindings. You can build and run the C++ handlers without LLVM.

The decoder first matches the exact scalar shape. It then tries the ModRM and
width/flags bindings. For some memory operands, the decoder masks the ModRM
bits that the kernel gets from the decoded effective address. If neither table
matches, the decoder selects a scalar binding for the slot count. The
architectural catalog selects mandatory-prefix variants such as MMX or SSE.

Fusions also come from recorded profiles. The checked-in sequence manifest
contains canonical instruction IDs. The decoder selects a fusion when all of
its operand shapes match an entry in the baked plan. Other sequences use their
scalar handlers. The LLVM JIT re-decodes guest bytes with architectural handlers
to build exact block recipes. See [the JIT options](instruction-specialization.txt).

## Training and updating the default table

Use a framework-dependent Podish CLI built from the same checkout, with a
matching managed block-dump ABI. Prepare two IA-32 Linux root filesystems.

| Input | Contents |
| --- | --- |
| Node rootfs | Node.js 14.21.3 at `/opt/node14`. Install fast-deep-equal 3.1.3, enhanced-resolve 5.27.0 and terser 5.51.2 under `/work/webpack/node_modules`. |
| Suite rootfs | GNU gzip, gunzip, sed, grep, sort, sha256sum and tar at the paths in the [workload runner](../benchmark/optimization/run_matrix.py). Provide a `/work/manual` mount point and IA-32 CoreMark binaries at `/work/coremark_zig` and `/work/coremark_gcc`. |

The [training runner](../scripts/run_modrm_training.py) resolves and minifies
fast-deep-equal, checks eight GNU commands against a deterministic 2 MiB fixture,
and verifies the CoreMark CRC results after 60,000 iterations of each build.
It turns off online LLVM JIT, dynamic machine-code specialization and the linked
cache for capture.

From the repository root, configure a shared host build with Block Profile
enabled and static specialization disabled.

```sh
cmake -S libfibercpu -B out/train -DCMAKE_BUILD_TYPE=Release \
  -DFIBERCPU_ENABLE_BLOCK_PROFILE=ON \
  -DFIBERCPU_STATIC_SPECIALIZATION=OFF \
  -DFIBERCPU_MODRM_TRAIN_PODISH=/absolute/path/to/Podish.Cli.dll \
  -DFIBERCPU_MODRM_TRAIN_ROOTFS=/absolute/path/to/node-rootfs \
  -DFIBERCPU_MODRM_TRAIN_SUITE_ROOTFS=/absolute/path/to/suite-rootfs

cmake --build out/train --config Release --target fibercpu_train_modrm_suite
```

The suite writes one BLK2 v3 capture per case and `modrm-hotness.json` under
`out/train/modrm-training-suite/Release/`. The collector estimates instruction
visits from block-entry counts and selects enough keys to cover 99.9% of each
case's weighted ModRM visits. It takes the union of those keys so Node.js and
the GNU tools keep their coverage even when CoreMark enters more blocks. Check
the per-case coverage in the JSON before replacing the default.

Copy the candidate into the repository once you finish the review. Regular
CMake builds read the checked-in table.

```sh
cp out/train/modrm-training-suite/Release/modrm-hotness.json \
  libfibercpu/src/generated/modrm-specializations.json
```

Use `FIBERCPU_MODRM_PLAN_FILE` to test a candidate without changing the default.
The `fibercpu_train_modrm` target runs only the short JavaScript workload.
To rebuild a candidate from existing profiles, use
[collect_modrm_hotness.py](../scripts/collect_modrm_hotness.py) with
`--per-profile --scalar-policies` and the unfused `*/blocks.bin` files from the suite.

The CMake training targets retain the observed operand width and flag liveness
for catalogued scalar kernels. `scalar_policies` attaches that data to the
selected ModRM keys. The decoder uses the decoded prefix and flag-liveness
result to select a policy. It falls back to the ModRM-only binding or the scalar
slot binding when the plan omits a policy.

An unfused suite build also writes candidate `baked-fusions-v3.json`,
`baked-fusion-operands-v3.json`, and `baked-scalar-operands-v3.json` files
alongside the ModRM plan. The scalar collector takes up to 1,024 shapes from
fast-deep-equal, 384 from each CoreMark build, and 256 from each GNU workload,
then deduplicates the union. The collector
ranks exact operands and their canonical sequence together. A candidate's
score is its estimated dispatch savings divided by guest instruction visits,
summed across workloads. The collector replays each capture with the same
longest-first matching as the decoder and keeps an operand shape only when
it increases the estimated savings. The operand plan records estimated
dispatch savings per workload for review. The default training budget is
384 sequences and 768 exact shapes. `BASE` uses the first 32 sequences.

Relative control transfers share an operand shape across encoded offsets.
The decoder resolves the target into the runtime slot and retains the
encoding for profile export. The collectors group these offsets before
ranking variants, and the code generators accept older v3 plans by merging
the equivalent entries. Data immediates and the stack adjustment of
`RET imm16` still distinguish shapes.

To refresh the fusion manifests from existing unfused profiles, run the
collector and regenerate both C++ tables.

```sh
python3 scripts/collect_baked_fusions.py \
  out/train/modrm-training-suite/Release/*/blocks.bin \
  --candidates benchmark/optimization/manifests/baked-fusions-v3.json \
  --operands benchmark/optimization/manifests/baked-fusion-operands-v3.json

dotnet build Podish.PerfTools/Podish.PerfTools.csproj -c Release
dotnet Podish.PerfTools/bin/Release/net10.0/Podish.PerfTools.dll gen-superopcodes \
  --input benchmark/optimization/manifests/baked-fusions-v3.json --top 32 \
  --output libfibercpu/src/generated/superopcodes.generated.cpp
dotnet Podish.PerfTools/bin/Release/net10.0/Podish.PerfTools.dll gen-superopcodes \
  --input benchmark/optimization/manifests/baked-fusions-v3.json --top 384 \
  --output libfibercpu/src/generated/superopcodes.extended.generated.cpp
```

Recapture profiles after changing the canonical instruction identities or the
decoded operand representation. Build the candidate, run the native and
PerfTools tests, and validate the workloads before replacing the default.
Use `FIBERCPU_GENERATED_SUPEROPCODES_FILE` and `FIBERCPU_OPERAND_PLAN_FILE`
to build with the candidate source and its matching operand plan without
changing the checked-in tables. Copy the suite's scalar plan to the checked-in
manifest when updating the defaults. Use `FIBERCPU_STATIC_OPERAND_PLAN_FILE`
to select a candidate scalar plan or pass an empty value to use ModRM-only
scalar binding.

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
