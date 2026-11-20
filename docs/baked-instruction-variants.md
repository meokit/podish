# Baked instruction variants

The 16-byte interpreter takes scalar native specializations from
[the checked-in ModRM table](../libfibercpu/src/generated/modrm-specializations.json).
CMake runs the Python catalog generator and compiles the table's entries into
libfibercpu. Each entry fixes the canonical training ID's ModRM byte and slot
count. The decoder normalizes memory ModRM where the kernel already uses the
decoded effective address. The table contains numerical IDs and the generator
rejects unknown IDs, duplicate keys and illegal encodings.

An unlisted shape uses the instruction's architectural handler with its slot
count bound at decode time. That handler obtains ModRM from the 16-byte op
payload. Two-slot instructions keep their adjacent immediate slot in both
paths. Mandatory-prefix decoding such as MMX versus SSE stays in the
architectural catalog. It is needed to execute the instruction correctly.

Fusions also come from recorded profiles. The checked-in sequence manifest
contains canonical instruction IDs. A fusion is selected only when all of its
decoded operands match an entry in the baked fusion operand plan. Unmatched
sequences dispatch through their individual instructions. The LLVM JIT
re-decodes from architectural handlers and builds its own exact block recipes
from the observed operands. Its availability and runtime selection follow the
normal JIT settings.

## Training and updating the default table

Use a framework-dependent Podish CLI built from the same checkout, with a
matching managed block-dump ABI. Prepare two IA-32 Linux root filesystems.

| Input | Contents |
| --- | --- |
| Node rootfs | Node.js 14.21.3 at /opt/node14. Install fast-deep-equal 3.1.3, enhanced-resolve 5.27.0 and terser 5.51.2 under /work/webpack/node_modules. |
| Suite rootfs | GNU gzip, gunzip, sed, grep, sort, sha256sum and tar at the paths in the [workload runner](../benchmark/optimization/run_matrix.py). Provide an empty /work/manual mount point and IA-32 CoreMark binaries at /work/coremark_zig and /work/coremark_gcc. |

The suite resolves and minifies fast-deep-equal, runs the eight GNU cases with
the deterministic 2 MiB fixture, and executes both CoreMark builds for 60,000
iterations. It checks the GNU outputs and both CoreMark CRC results. Training
uses native dispatch with online LLVM JIT, dynamic machine-code specialization
and the linked cache disabled.

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

The suite writes one BLK2 v3 capture per case and
`modrm-hotness.json` under `out/train/modrm-training-suite/Release/`.
For each case, the collector selects enough keys to cover 99.9% of its
block-entry-weighted ModRM visits. The default uses the union, so CoreMark's
loop counts cannot crowd out the GNU tools or Node.js. The JSON includes
per-case coverage for review. Weights estimate instruction visits from block
entry counts.

Review the candidate and copy it into the repository explicitly. Regular
CMake builds use the existing checked-in table. They do not run the suite.

```sh
cp out/train/modrm-training-suite/Release/modrm-hotness.json \
  libfibercpu/src/generated/modrm-specializations.json
```

Use `FIBERCPU_MODRM_PLAN_FILE` to test a candidate without changing the default.
The `fibercpu_train_modrm` target runs only the short JavaScript workload.
To rebuild a candidate from existing profiles, use
[collect_modrm_hotness.py](../scripts/collect_modrm_hotness.py) with
`--per-profile --scalar-policies` and the unfused `*/blocks.bin` files from the suite.

The CMake training targets also retain the observed operand width and flag
liveness for catalogued scalar kernels. The output's `scalar_policies`
entries refer to the same selected ModRM keys. The decoder matches the policy
against the decoded prefix and current flag-liveness result. A missing policy
uses the ModRM-only variant or the architectural handler with its slot count
bound. Existing plans can omit `scalar_policies`.

An unfused suite build also writes candidate `baked-fusions-v3.json` and
`baked-fusion-operands-v3.json` files alongside the ModRM plan. The collector
ranks exact operands and their canonical sequence together. A candidate's
score is its estimated dispatch savings divided by guest instruction visits,
summed across workloads. The collector replays each capture with the same
longest-first matching as the decoder and keeps an operand shape only when
it increases the estimated savings. The operand plan records estimated
dispatch savings per workload for review. The default training budget is
128 sequences and 256 exact shapes. `BASE` uses the first 32 sequences.

Use the same offline collector to refresh the manifests from existing
unfused profiles:

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
  --input benchmark/optimization/manifests/baked-fusions-v3.json --top 128 \
  --output libfibercpu/src/generated/superopcodes.extended.generated.cpp
```

Recapture profiles after changing the canonical instruction identities or the
decoded operand representation. Build the candidate, run the native and
PerfTools tests, and validate the workloads before replacing the default.
Use `FIBERCPU_GENERATED_SUPEROPCODES_FILE` and `FIBERCPU_OPERAND_PLAN_FILE`
to build with the candidate source and its matching operand plan without
changing the checked-in tables.

## Code generation review

Inspect the emitted machine code as part of a table update.

| Path | Expected generated code |
| --- | --- |
| Baked scalar | The fixed ModRM operand reaches the kernel as a literal. Register selection and the ModRM register-versus-memory decision disappear when the kernel uses them. The instruction advances one or two slots with a fixed displacement. A ModRM-only key leaves other operands dynamic. A matching scalar policy additionally fixes the width and omits updates to dead flags where specified. |
| Baked fusion | The exact operand records supply the ModRM, immediates and slot count of every constituent. The final handler performs one tail dispatch. Each memory operation retains its restart/retry exit. |
| LLVM exact | The block compiler inlines the operation templates with their decoded operands before running the LLVM optimization pipeline. On the ordinary path, a baked memory immediate should appear as an instruction constant and the PC for replay should be a fixed offset from the original slot pointer. Branch targets and links still come from block runtime storage. |

On ARM64, use `llvm-nm` and `llvm-objdump -d` on libfibercpu to locate and inspect
`DispatchWrapper` instantiations with `WithModRM` and `SlotBound` in the
signature. Include a register form and a two-slot memory-immediate form.
For LLVM exact, use `JitMode=profile` and `WeightedProfilePath` to capture the
probe recipe. `fibercpu_llvm_cache --linked PROFILE BUNDLE --llvm-opt-level O2`
then emits machine-code objects using the same exact-operand preparation as the
online compiler. Extract and disassemble the matching object with
[extract_linked_block_cache.py](../scripts/extract_linked_block_cache.py).
Also run the probe with `JitMode=exact` to exercise execution and replay.

Use `-XX:LlvmFoldOperandLoads=on` for the extra post-inline folding pass in
the online exact compiler. To compare offline objects from the same profile,
build once with `FIBERCPU_LLVM_FOLD_OPERAND_LOADS=1` and once with `=0`.

The initial ARM64 review counted 1,258 baked scalar symbols, including 17
two-slot bindings. The inspected Group 1 register add advanced 16 bytes. The
two-slot memory compare loaded its immediate from offset 24 and advanced 32
bytes. Both selected the ModRM operation at compile time. The LLVM exact
probe materialized `0x12345678` with `mov` and `movk`, inlined the instruction
bodies and retained the MMU replay and block-link exits.
