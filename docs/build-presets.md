# Native build presets

Standalone CMake, native Podish and Podish.Browser default to `3m` for both guests.
The three presets select i386 and AArch64 instruction tables calibrated on macOS ARM64.

| Preset | Text growth limit per guest | Text growth limit with both guests |
| --- | ---: | ---: |
| `1.5m` | 1,572,864 B | 3,145,728 B |
| `3m` | 3,145,728 B | 6,291,456 B |
| `10m` | 10,485,760 B | 20,971,520 B |

The names use MiB of growth relative to an unbaked library with the same
guest selection, compiler, and optimization flags. The trainer measures each
guest against its own unbaked build. Both guests use generalization-aware marginal ranking with risk 0.5. Larger tiers retain every binding in smaller tiers. The i386 ranking exhausts its positive-gain candidates at 3 MiB, so its 3 and 10 MiB presets contain the same bindings. i386 tiers include ModRM,
width and flags, exact and partial scalar bindings, and SuperOpcodes.
AArch64 tiers include exact words, partial operand shapes, pairs, and longer
fused sequences. Both guests train against the same workloads.

The i386 calibration used Apple Clang 21 and LTO. The AArch64 retraining
used Homebrew Clang 21.1.8 and Release ThinLTO on macOS ARM64. Sizes and
increases below are in bytes, measured with one guest per build.

| Preset | i386 native text | i386 growth | AArch64 native text | AArch64 growth |
| --- | ---: | ---: | ---: | ---: |
| Unbaked | 531,496 | 0 | 320,072 | 0 |
| `1.5m` | 2,057,612 | 1,526,116 | 1,188,416 | 868,344 |
| `3m` | 2,269,488 | 1,737,992 | 1,548,964 | 1,228,892 |
| `10m` | 2,269,488 | 1,737,992 | 2,543,476 | 2,223,404 |

The compact AArch64 tiers prioritize scalar partials and dynamic-immediate
pairs, with sequence caps of 1,850 for `1.5m` and 2,600 for `3m`. The
`10m` tier retains the full positive-marginal Method C table. With both
guests enabled, Homebrew Clang 21.1.8 and Release ThinLTO produced 3,763,636 B
of native text for the default `3m`, up 2,980,776 B from the matched
unbaked library's 782,860 B. See the [AArch64 retraining measurements](../benchmark/optimization/results/20261129-a64-retrain-presets/README.md).

## Build libfibercpu

Use Clang and Ninja. From the repository root:

```sh
cmake -S libfibercpu --list-presets
cmake -S libfibercpu --preset 1.5m
(cd libfibercpu && cmake --build --preset 1.5m)
```

Substitute `3m` or `10m` in both commands for a larger preset. Each writes
to `build/native/<preset>/`. These CMake presets use Release and disable
Host LLVM JIT and runtime specialization.

For an existing build directory, a custom generator, or a toolchain file,
set the selector directly:

```sh
cmake -S libfibercpu -B build/native/my-build -DCMAKE_BUILD_TYPE=Release \
  -DFIBERCPU_BAKING_PRESET=3m
cmake --build build/native/my-build --config Release --target fibercpu
```

Use `FIBERCPU_BAKING_PRESET=custom` to set the baking options individually.
Its default inputs are the `3m` tables. The explicit `BASE` fusion strategy
selects the legacy 32-sequence table.
The GPR pin, instrumentation, JIT, and dynamic specialization settings
remain independent. `X86_GetOptimizationConfig()` reports the selected
preset as `baking_preset`.

Set `-DFIBERCPU_DATA_TLB_ENTRIES=512` to change the capacity of each software
read and write TLB table for both guests. The default is 1024 entries per table.
The value must be a power of two up to the 4 GiB guest address space's page
count (1,048,576). The software instruction TLB has 256 entries.
`X86_GetOptimizationConfig()` reports the capacity as `data_tlb_entries`.

Named native shared-library builds report the linked text size where the
format can be measured. They use `__text` on macOS and `.text` on Linux and
Windows, and report the largest slice of a universal Mach-O. Training enforces
the growth limit against the measured baseline. Ordinary builds report total
native text and the applicable growth budget. Other toolchains can produce
different sizes from the same tables. Static archives and browser Wasm builds
use the same tables, with final code size determined by the application link.

Existing CMake cache values and MSBuild properties using `1.75m` or `3.5m`
resolve to `1.5m` or `3m` respectively.

## Build Podish with a preset

```sh
dotnet build Podish.Cli/Podish.Cli.csproj -c Release -p:FibercpuBakingPreset=1.5m
```

For Windows x64 add `-r win-x64 -m:1` as in the ordinary CLI build. The
`1.5m` and `10m` choices use `Fiberish.X86/build_native/<rid-or-host>/<preset>/`
for the native build and `bin/<preset>/` and `obj/<preset>/` for managed outputs.
The `3m` and `custom` selections use the ordinary output locations. Select
`-p:FibercpuBakingPreset=custom` when supplying individual baking options.

## Update the baked inputs

Each [i386 preset directory](../libfibercpu/presets/) holds `modrm.json`,
`scalar.json`, `fusion-operands.json`, and the fusion recipes in
`recipes.json`. Regenerate `superopcodes.generated.cpp` from `recipes.json`
using `Podish.PerfTools gen-superopcodes` with `--top` set to the number
of recipes. CMake consumes the checked-in source and generates the operand
bindings from JSON during the build. The corresponding AArch64 plan lives
in `presets/aarch64/<tier>/scalar.json`.

Use the [training workflow](baked-instruction-variants.md) for new candidates.
Check binding containment and calibrate each tier on macOS ARM64 before
replacing the shipped tables. Use the standalone checker with a matched
unbaked library when checking growth. This example uses i386-only builds:

```sh
python3 scripts/check_native_text_budget.py --macho-size /usr/bin/size \
  --baseline build/native/unbaked/libfibercpu.dylib \
  --growth-limit 1572864 build/native/1.5m/libfibercpu.dylib
```
