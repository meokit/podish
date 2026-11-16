# CPU instruction tests

This project tests the native IA-32 user-mode core directly. It does not create an
`Engine`, Linux runtime, rootfs, scheduler, VFS, or network stack.

Current measured results and remaining failures: [`tests/cpu/RESULTS.md`](../tests/cpu/RESULTS.md).
The complete quick suite deliberately remains red while exact x87 discrepancies
are unresolved; no failing corpus expectations are replaced or silently skipped.

## Run

```sh
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --filter "Category!=Exhaustive" --blame-hang-timeout 3m
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release --no-build --filter "Category=Exhaustive" --blame-hang-timeout 5m
```

For a diagnostic serial baseline (not the default test policy), pass
`--settings Fiberish.X86.Tests/validation.serial.runsettings` and compare the
per-case outcomes to the parallel TRX report.

Use `--filter FullyQualifiedName~...` to select a family or a reported test case.
The TRX/console diagnostics include operands or upstream case paths. A test row
may execute many explicitly identified vectors; report both xUnit row counts and
vector counts rather than presenting them as the same coverage metric.

The repository targets .NET 10. This test project permits a newer installed major
runtime so a machine with only .NET 11 can also run it. Native build prerequisites
are the same as `Fiberish.X86`; NASM and networking are not needed to replay the
checked-in imported test assets.

## Isolation and parallelism

- Every vector owns a `CpuSandbox` and a native engine/MMU via a `SafeHandle`.
  Dispose runs on failure as well as success. Engines and writable pages are never
  stored in static fixtures or pools.
- Each sandbox explicitly zeroes its allocated pages. Guest addresses may repeat
  between instances because their address spaces are independent.
- Process fatal signal handlers are disabled using the create API's per-call
  option, not by changing global environment variables.
- Pure static generators/constants are permitted; mutable static CPU, callback,
  random, expected-data, or asset caches are not.
- xUnit runs separate family classes concurrently (bounded to four workers).
  Theory rows in one class are not assumed to run concurrently. The explicit
  bounded-concurrency regression verifies native instances independently of the
  framework's scheduling.
- Native floating environment save/restore is an execution-boundary requirement;
  thread-local storage alone is not sufficient isolation.

## Oracle rules

Integer results use independent, width-bounded managed models. Imported FEX
results come from the pinned upstream configuration. Architectural undefined
bits are masked separately from preserved bits; a zero shift count must preserve
flags rather than ignoring them. Floating point cases use exact input bit
patterns where representation matters.

Tests intentionally do not accept an arbitrary native fault as success. Exception
tests check its vector and restart EIP. Imported HLT sentinels are recognized at
decoded instruction boundaries, not by looking for a byte anywhere in the binary.
Asset hashes and upstream provenance are checked separately from CPU results.

## Optimized execution

Both Step and bounded Run paths are exercised. Run is not treated as a precise
single-instruction API: instruction budgets are block-oriented. Warm-code and
rewrite regressions check that decode caches cannot leak across engines.

For superopcode OFF use a separate build/output, or rebuild and launch a fresh
testhost before running:

```sh
dotnet test Fiberish.X86.Tests/Fiberish.X86.Tests.csproj -c Release -p:NativeBuildDir=build_native/cpu-nosuper -p:EnableSuperOpcodes=false --filter "Category!=Exhaustive"
```

Never switch a loaded native DLL underneath a running testhost. The dedicated
`cpu-tests.yml` workflow uses fresh jobs for the ON/OFF matrix, runs the corpus on
Linux/Windows/macOS when triggered, and schedules byte-arithmetic exhaustiveness
separately. Those remote jobs have not yet been run for this worktree. When
switching local build variants, verify the copied DLL matches the selected build;
`PreserveNewest` alone can retain a newer DLL from another variant.

## Scope

This is an expanding suite, not a claim that every IA-32 form has been verified.
The target is the user-mode baseline and extensions advertised by the core's
CPUID, not BIOS/protected-mode OS execution, AVX, or all 64-bit x86 instructions.
See `docs/cpu-instruction-test-strategy.md` for the design and `tests/cpu/` for
upstream inventory, asset regeneration, and explicit exclusion reasons.

Managed coverage tools do not measure the native handlers. ISA-form, operand-edge,
exception, Step/Run, and native branch coverage must remain distinct metrics.
