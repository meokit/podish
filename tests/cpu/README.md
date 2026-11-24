# Pinned CPU instruction corpus

The C# xUnit project `Fiberish.X86.Tests` executes **204 upstream FEX tests**, including
**137 x87 tests with defined portable results** and **14 FEX regression tests**. No network access, assembler,
Linux process, or upstream FEX installation is needed to discover or execute tests.
The import tool is a separate, optional C# console application.

See [RESULTS.md](RESULTS.md) for actual replay outcomes and known failures. The
inventory below is an execution-scope catalog, **not** a count of passing tests.

## Provenance and layout

Upstream: <https://github.com/FEX-Emu/FEX>

Pinned commit: `8e95a994a014b0e68fa38b7bf17d84987102e374`

License: MIT; the unchanged license is at `upstream/fex/LICENSE` and is also
distributed with executable assets at `assets/fex/LICENSE`.

```text
tests/cpu/
  fex.lock.json                  # 265 original files, raw URLs and SHA256 hashes
  upstream/fex/                  # unchanged original bytes, including:
    LICENSE
    unittests/32Bit_ASM/          # all 260 .asm files, x87_FIP.mac, CMakeLists.txt
    Source/Tools/CommonTools/HarnessHelpers.h
    Scripts/json_config_parse.py
  assets/fex/
    LICENSE
    catalog.json                 # all 260 sources; ready/blocked + concrete reason
    bin/<upstream-relative>.bin   # all 260 offline NASM conversions
    manifests/<relative>.json    # CONFIG, provenance, binary SHA256, HLT addresses
  tools/FexImport/                # standalone net11.0 console import/verification tool
Fiberish.X86.Tests/Fex/           # strict CONFIG parser, asset verifier, execution, shards
```

The test project links `tests/cpu/assets/fex/**/*` to output
`Assets/Fex/%(RecursiveDir)%(Filename)%(Extension)`. Only this asset directory is
needed at runtime; original source files and importer build output are not copied.
The local `.gitignore` deliberately makes the precompiled `assets/fex/bin/` files
trackable despite the repository-wide .NET `bin/` ignore.

## Coverage catalog

| Upstream directory | Sources | Runnable | Blocked |
| --- | ---: | ---: | ---: |
| Primary | 50 | 38 | 12 |
| PrimaryGroup | 12 | 12 | 0 |
| Secondary | 4 | 1 | 3 |
| SecondaryModRM | 2 | 0 | 2 |
| TwoByte | 2 | 0 | 2 |
| VEX | 32 | 0 | 32 |
| X87 | 138 | 137 | 1 |
| FEX_bugs | 18 | 14 | 4 |
| root | 2 | 2 | 0 |
| **Total** | **260** | **204** | **56** |

Blocked tests are inventory entries, **not skipped xUnit placeholders**. Reasons
are stored per test in `assets/fex/catalog.json`:

- One FYL2XP1 source supplies ST(0)=2, outside its defined range; its expected
  result is not a portable oracle (see [X87-NOTES.md](X87-NOTES.md)).
- 32 declare AVX, which requires an unavailable 256-bit vector execution contract.
- One additional regression, `VEXW_Bug`, uses AVX/FMA without declaring that feature.
- 12 explicitly manipulate segment selectors (including IRET's implicit CS load); their meaning depends on Linux/FEX
  descriptors rather than merely flat zero-base memory addressing.
- Three expect FEX/Linux-specific GDTR, LDTR, or CR0 contents.
- Two declare Linux or CLZERO host features, respectively.
- Two request FEX-specific x87 FIP/reduced-precision environment settings.
- One invokes Linux `int 0x80`.
- Two (`TwoByte/0F_82` and `TwoByte/0F_82_2`) synthesize a terminating HLT outside
  the source image, requiring their own reviewed dynamic-code termination contract.
  They are distinct from the enabled primary CALL/JMP wrapping cases described below.

An unsupported instruction in a **runnable** test fails normally. Numeric output
mismatches, faults, instruction limits, malformed/unknown CONFIG fields, and missing
or corrupted assets are failures, never implicit passes or automatic exclusions.
The inventory test pins the runnable count and the 137 defined-result x87 cases so coverage cannot
silently shrink. Enabling a blocked test requires implementing its contract,
regenerating its disposition, and updating that reviewed inventory assertion.

## Execution semantics

Eight distinct `FexShard00Tests` through `FexShard07Tests` classes provide actual
xUnit collection-level parallelism. Each theory row receives only an immutable test
ID and creates/disposes a new `CpuSandbox(false)`, with its own CPU/MMU/backing
pages. There are no shared CPU fixtures, pools, mutable static caches, random data,
global callbacks, or process-level signal handlers. Default xUnit parallelization
must remain enabled.

The upstream 32-bit CMake wrapper prepends `BITS 32`, `org 10000h`,
`mov eax,0x17; mov ds,ax; mov es,ax`, then appends `ret`. Our binary conversion keeps
the mode, origin, EAX side effect, original assembly body, and appended RET, but
**replaces the wrapper's DS/ES selector loads with four one-byte NOPs**: sandbox
segments already have base zero, and installing Linux selectors would be incorrect
for its flat model. Keeping their four bytes is essential for hard-coded GOT and
return-address expectations; removing them relocates the entire test incorrectly.
Tests explicitly manipulating selectors are separately blocked; source is never
rewritten to remove test instructions. NASM wrappers and listings are temporary
files under the importer's ignored `obj/nasm/` directory.

Mappings follow pinned `HarnessHelpers.h:493-537` with fixed 4 KiB guest pages:

- Source image at `0x00010000`, rounded up to pages.
- Zeroed scratch: `0xE0000000`, ten pages.
- Zeroed SIB scratch: `0xE7FFF000`, two pages around `0xE8000000`.
- Zeroed stack: `0xC0000000`, one page; sandbox ESP begins at `0xC0001000`.
- Additional zeroed regions are mapped exactly as CONFIG declares.
- All these mappings are RWX, as in the upstream runner. This includes the
  generated-code indirect-call regression at `0x80000000`.

CONFIG is strict: `Mode`, `RegData`, `MemoryRegions`, `MemoryData`, `HostFeatures`,
and `Env` are the only recognized keys. Mode must be `32BIT`; unsupported declared
features/settings are catalogued as blocked. Duplicate keys, unsupported register
names/widths, overflowing addresses, and invalid ranges are rejected.
`MemoryData` initializes memory using FEX's low-byte-first hexadecimal-word
encoding, and must lie in a mapped region. **RegData is expected final output,
never input state.** Unspecified initial registers use the native reset state,
apart from sandbox ESP and the retained assembled EAX prelude.

GPR and XMM expectations are exact. `MMn` with two values means a **physical**
x87 80-bit slot: significand plus sign/exponent. Since `ReadFpu` reads logical ST,
the runner uses `logical = (physical - FSW.TOP + 8) & 7`. A one-value `MMn`
expectation checks the physical MMX low 64 bits. Comparisons are exact except the
FPTAN(1) hardware sample, which allows one binary80 significand unit, with exact
sign/exponent; an independent test pins correctly rounded tan(1). See
[X87-NOTES.md](X87-NOTES.md) for the Intel accuracy contract. No rounding-to-double
or register-name reinterpretation is applied.

For termination, the importer obtains HLT addresses from NASM **instruction listing
records**, not byte searches. At runtime Iced decodes the instruction at the actual
live 32-bit EIP. Only a decoded `Code.Hlt` at a source-listed address in the hashed
binary terminates a case, **before HLT executes**. Every other instruction is
single-stepped, with status checked and a finite 1,000,000-instruction budget.
Some source loops deliberately execute more than 65,536 iterations, so a 100,000
instruction budget would incorrectly reject valid terminating tests.
Neither an arbitrary #GP nor an embedded `F4` byte is success. Dynamic calls may
execute in explicitly mapped pages and return to the original source image. The
two primary CALL/JMP wrapping cases declare the source-generated six-byte
`mov eax,41424344h; hlt` image at `E0000000`; a dynamic HLT is accepted only at the
declared offset after comparing the entire image. Every other unlisted dynamic
HLT is rejected. These corpus tests exercise `Step`; focused
harness tests cover `Run` independently.

## Reproduce, update, and verify

From the repository root:

```powershell
# Offline: verify original inventory, source SHA256, manifests, binary hashes and CONFIG.
dotnet run --project tests/cpu/tools/FexImport -- verify

# Explicit network operation: fetch the pinned archive, not a moving branch.
# Existing source locks must match exactly before files are written.
dotnet run --project tests/cpu/tools/FexImport -- fetch

# Explicit offline regeneration with NASM 3.01; no PATH or machine configuration change.
dotnet run --project tests/cpu/tools/FexImport -- regenerate --nasm C:\path\to\nasm.exe

# Framework discovery/execution (requires the normal project's native build setup).
dotnet test Fiberish.X86.Tests --filter FullyQualifiedName~Fex
```

The importer targets `net11.0` and allows major runtime roll-forward. It shares only
the managed schema/parser source with the test project and has no package or native
project dependency. `--root <repository-path>` is supported from another directory.
`fetch` uses GitHub's pinned codeload archive in memory and extracts only the locked
files. Every original file retains its own raw pinned URL and SHA256 in
`fex.lock.json`. The API and its authentication/rate limits are not required.

Initial generation used official portable NASM 3.01 from
<https://www.nasm.us/pub/nasm/releasebuilds/3.01/win64/nasm-3.01-win64.zip>,
extracted outside the repository without installing global tools. Runtime testing
never executes NASM or the importer. Regeneration records NASM version `3.01`, not
host-specific build dates or temporary paths; repeated conversions are byte-stable.

To update the upstream revision, review the license/inventory and explicitly change
the pin in the shared managed schema and source lock. Do not replace upstream
expectations with locally observed values to make emulator failures disappear.
