# CPU instruction validation results

Measured 2026-11-16 in worktree/branch `tests/cpu-instruction-tests`, before
integration into `main`. This is a local validation record, not a claim of complete
ISA coverage or a green suite. Commit/integration status is recorded in Git history.

## Environment and final outcomes

- Windows x64; .NET SDK/runtime 11.0.100/11.0.0 running the net10.0 test project
  with its explicit major-version roll-forward policy.
- Native Release build using Visual Studio's clang-cl, CMake/Ninja and SoftFloat ON.
- Each test vector owns its CPU/MMU/pages. Default xUnit concurrency is four workers.

| Run | Passed | Failed | Skipped | Total xUnit rows |
| --- | ---: | ---: | ---: | ---: |
| Quick suite, superopcodes ON, parallel | 2205 | 6 | 0 | 2211 |
| Quick suite, superopcodes ON, serial | 2205 | 6 | 0 | 2211 |
| Quick suite, superopcodes OFF, parallel | 2205 | 6 | 0 | 2211 |
| Quick suite after restoring ON DLL | 2205 | 6 | 0 | 2211 |
| Exhaustive byte ADD/SUB, ON | 32 | 0 | 0 | 32 |
| Existing targeted regression suite | 50 | 0 | 0 | 50 |

The serial/parallel and ON/OFF TRX reports were compared **by test name, outcome,
and full failure message**, not just totals; all 2211 entries matched. The OFF
build used `build_native/cpu-nosuper`; its DLL hash and CMake option were checked.
The final managed/test output DLL was restored from `build_native/host` (ON),
hash-verified, and the quick suite was replayed in a new testhost.

The exhaustive 32 rows execute **131,072 distinct operand vectors**: every pair
of byte inputs for ADD and SUB, with a fresh sandbox per vector. These are not
131,072 separately discovered xUnit tests, and do not exhaust ADC/SBB or wider ALU
operand spaces.

The quick suite includes **205 upstream FEX cases: 199 pass, six fail**. Its other
**2006 rows all pass**, including independent integer/SIMD/addressing boundaries,
exception restart state, x87/FX state regressions, lifecycle/concurrency,
host-floating-environment restoration, callback reentry and static-state checks.

The existing targeted run covers `SseMoveInstructionTests`,
`SseCvttInstructionTests`, `StrtodInstructionTests`, and `EngineMmuTransferTests`.
It is not the entire Linux/runtime integration suite. Its cross-page code-cache
regression now passes without changing the existing assertion.

## Remaining exact-result failures

All six remain ordinary failing tests. No tolerances, substituted upstream
answers, expected-failure passes or skipped placeholders were introduced.

| FEX ID | Instruction | Expected significand | Actual significand | Assessment |
| --- | --- | --- | --- | --- |
| `X87/D9_F2` | FPTAN | `C75922E5F71D2DC6` | `C75922E5F71D3000` | binary64 transcendental precision |
| `X87/D9_F3` | FPATAN | `C90FDAA22168C235` | `C90FDAA22168C000` | binary64 transcendental precision |
| `X87/D9_FB` | FSINCOS | `8A51407DA8345C92` | `8A51407DA8346000` | binary64 transcendental precision |
| `X87/D9_FE` | FSIN | `D76AA47848677021` | `D76AA47848677000` | binary64 transcendental precision |
| `X87/D9_FF` | FCOS | `D51132BA9B902522` | `D51132BA9B902800` | binary64 transcendental precision |
| `X87/D9_F9` | FYL2XP1 | `8000000000000000` | `BE320C4B2E0C9000` | upstream input outside documented range |

The first five require a reviewed extended-precision implementation and proper
x87 numerical/exception behavior, not hard-coded answers. For FYL2XP1, upstream
uses ST(0)=2 and expects 8; this is outside the instruction's documented input
range and is not a universal mathematical oracle. See [X87-NOTES.md](X87-NOTES.md).
There are additional unverified ISA forms/flags/exception paths; six failures
does **not** mean six remaining defects in the entire emulator.

## Implemented corrections exercised by these tests

- Shared Step/Run prefix specialization and relative-branch target setup; precise
  bounded fetch without speculative byte-16/page faults; invalid LOCK validation.
- 16-bit address-size decoding/wrapping, default DS/SS selection and specialized
  MOV/LEA address calculation; retain mapped cross-page code blocks and invalidation.
- Rotate count masking/carry behavior, wide-count SAR, host-safe signed divide
  overflow and restart state; word-immediate PUSH; AAA/AAS AX carry/borrow;
  ARPL and legacy SALC.
- Explicit guest SIMD conversion rounding and MXCSR exception status rather than
  relying on the host floating environment.
- FXSAVE/FXRSTOR legacy layout, logical/physical stack mapping, x87 environment
  layouts/metadata, MXCSR validation, FISTTP64, FSCALE, FPREM results, constants and
  exceptional trig operands; MMX TOP/tag/high-word synchronization and EMMS state.
- Scoped SoftFloat/TLS and host fenv restoration across success, faults and nested
  execution; per-call creation option avoids process fatal signal registration;
  immutable dispatch infrastructure initializes safely and no TLS specialization
  cache retains guest-derived state.

These statements describe the corrected paths, not full conformance of each
instruction family (for example, FPREM partial reduction/quotient flags remain
outside the numerical-result fix).

## Corpus integrity and scope

FEX is pinned to `8e95a994a014b0e68fa38b7bf17d84987102e374`.
The importer successfully regenerated the assets and verified all **265 original
files**, all 260 manifests/binaries and strict runnable CONFIG data. Original
assembly/CONFIG bytes and MIT notices are retained. Replay requires neither NASM
nor network access. The wrapper preserves original instruction addresses, and
the two enabled dynamic-HLT cases require their exact declared code image.

The catalog records **205 ready / 55 blocked**, including all **138 x87 sources**
as ready. Blocked entries have specific ISA, OS/descriptor, environment or dynamic
termination-contract reasons and do not produce xUnit skipped rows. See
[README.md](README.md) and `assets/fex/catalog.json` for the complete inventory.

First-party staged changes pass whitespace checks. Hash-locked assets retain
their exact CRLF bytes (`-text`), and the original upstream files retain ten
preexisting whitespace findings; neither is reformatted to satisfy Git's diff
checker. With `core.whitespace=cr-at-eol`, only those upstream findings remain.

## Reproduction and evidence

```sh
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --filter "Category!=Exhaustive" --logger "trx;LogFileName=cpu-final.trx"
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --no-build --filter "Category!=Exhaustive" --settings Fiberish.X86.Tests/validation.serial.runsettings --logger "trx;LogFileName=cpu-serial.trx"
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --no-build --filter "Category=Exhaustive" --logger "trx;LogFileName=cpu-exhaustive-final.trx"
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release -p:NativeBuildDir=build_native/cpu-nosuper -p:EnableSuperOpcodes=false --filter "Category!=Exhaustive" --logger "trx;LogFileName=cpu-nosuper.trx"
dotnet run --project tests/cpu/tools/FexImport -c Release -- verify
```

Quick runs correctly return exit code 1 because of the six visible failures.
Restore and verify the ON DLL when switching back from OFF (the content-copy
`PreserveNewest` policy can otherwise keep a newer OFF DLL).

Local ignored evidence files are under `Fiberish.X86.Tests/TestResults/`:
`cpu-final.trx`, `cpu-serial.trx`, `cpu-nosuper.trx`,
`cpu-final-restored-on.trx`, and `cpu-exhaustive-final.trx`.
The existing suite report is
`Fiberish.Tests/TestResults/cpu-existing-regressions-final.trx`.

The new `.github/workflows/cpu-tests.yml` configures Linux ON/OFF, Windows and
macOS jobs, corpus verification, uploaded TRX artifacts and scheduled byte-ALU
exhaustiveness. **No remote CI run, Linux run or macOS run has been performed**
for these changes at validation time. Known failures are not suppressed in CI.
