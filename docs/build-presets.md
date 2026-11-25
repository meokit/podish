# Native build presets

Standalone CMake and native Podish builds default to `3.5m`. Podish.Browser
defaults to `1.75m` with a multi-workload hot dispatch plan. The three presets
select instruction tables calibrated on macOS ARM64.

| Preset | Text size hint | Native text on macOS ARM64 with Clang 21 |
| --- | ---: | ---: |
| `1.75m` | 1,835,008 B | 1,756,056 B |
| `3.5m` | 3,670,016 B | 3,603,680 B |
| `10m` | 10,485,760 B | 9,387,872 B |

The names use MiB. Every target uses the same trained tables, which form
strict supersets from `1.75m` through `3.5m` to `10m`. Each tier includes
ModRM, width and flags, exact and
partial scalar bindings, and SuperOpcodes. The two smaller tiers use measured
points from the size scan. The largest extends the same training pool.

## Build libfibercpu

Use Clang and Ninja. From the repository root:

```sh
cmake -S libfibercpu --list-presets
cmake -S libfibercpu --preset 1.75m
(cd libfibercpu && cmake --build --preset 1.75m)
```

Substitute `3.5m` or `10m` in both commands for a larger preset. Each writes
to `build/native/<preset>/`. These CMake presets use Release and disable
Host LLVM JIT and runtime specialization.

For an existing build directory, a custom generator, or a toolchain file,
set the selector directly:

```sh
cmake -S libfibercpu -B build/native/my-build -DCMAKE_BUILD_TYPE=Release \
  -DFIBERCPU_BAKING_PRESET=3.5m
cmake --build build/native/my-build --config Release --target fibercpu
```

Use `FIBERCPU_BAKING_PRESET=custom` to set the baking options individually.
Its default inputs are the `3.5m` tables. The explicit `BASE` fusion strategy
selects the legacy 32-sequence table.
The GPR pin, instrumentation, JIT, and dynamic specialization settings
remain independent. `X86_GetOptimizationConfig()` reports the selected
preset as `baking_preset`.

Named native shared-library builds report the linked text size where the
format can be measured. They use `__text` on macOS and `.text` on Linux and
Windows, and report the largest slice of a universal Mach-O. The preset
size is a reference. Builds can exceed it because toolchains and architectures
emit different code. Static archives and browser Wasm builds can use the same
tables, with final code size determined by the application link.

## Build Podish with a preset

```sh
dotnet build Podish.Cli/Podish.Cli.csproj -c Release -p:FibercpuBakingPreset=1.75m
```

For Windows x64 add `-r win-x64 -m:1` as in the ordinary CLI build. The
`1.75m` and `10m` choices use `Fiberish.X86/build_native/<rid-or-host>/<preset>/`
for the native build and `bin/<preset>/` and `obj/<preset>/` for managed outputs.
The `3.5m` and `custom` selections use the ordinary output locations. Select
`-p:FibercpuBakingPreset=custom` when supplying individual baking options.

## Update the baked inputs

Each [preset directory](../libfibercpu/presets/) holds `modrm.json`,
`scalar.json`, `fusion-operands.json`, and the fusion recipes in
`recipes.json`. Regenerate `superopcodes.generated.cpp` from `recipes.json`
using `Podish.PerfTools gen-superopcodes` with `--top` set to the number
of recipes. CMake consumes the checked-in source and generates the operand
bindings from JSON during the build.

Use the [training workflow](baked-instruction-variants.md) for new candidates.
Check strict containment and calibrate each tier on macOS ARM64 before
replacing the shipped tables. Use the standalone checker with `--limit`
when you need a strict calibration check:

```sh
python3 scripts/check_native_text_budget.py --macho-size /usr/bin/size \
  --limit 1835008 build/native/1.75m/libfibercpu.dylib
```
