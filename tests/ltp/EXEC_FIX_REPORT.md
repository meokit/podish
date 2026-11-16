# execve boundary repairs and allocation work

Date: 2026-11-16. This follows the native oracles and pre-fix results in
[EXEC_BASELINE.md](EXEC_BASELINE.md). The standalone binary assertions are
unchanged; this round repairs the runtime, not the expectations.

## Repairs

- Main ELF, shebang interpreter and `PT_INTERP` use the same executable access
  check: regular file, noexec/seals, execute DAC, then writable file holds.
  `PT_INTERP` path lookup preserves its errno (`EACCES` is no longer flattened
  to `ENOENT`), and invalid interpreter format reports `ELIBBAD`.
  The process loader also supplies its credentials explicitly before a task
  owner exists, so bootstrap loading cannot bypass interpreter file/mount checks.
- Writable mmap holds participate in the inode write count, including private
  and read-only VMAs created from `O_RDWR`. Split/fork references keep that hold
  alive; final unmap releases it. No VMA scan or new tracking object is added.
- Set-ID computation honors the main executable's `MS_NOSUID` mount flag and
  requires `S_ISGID | S_IXGRP` together for EGID changes. One value-type identity
  is used for both staged auxv and the eventual credential commit, including
  resolved shebang interpreters. PT_INTERP's own set-ID bits/mount do not change
  the main file's credential policy.
- argv/envp consume the same remaining byte budget, including NULs, pointer
  slots and the copied exec filename. The 128-KiB floor and 6-MiB unlimited-stack
  cap are enforced. The loader
  rechecks rewritten shebang/public-loader inputs in its existing sizing pass.
- Recoverable staging and FD-unshare failures restore image metadata, layout,
  brk, vDSO addresses and syscall bindings, and dispose the staged image/engine.
  Once irreversible teardown starts, exceptions force SIGSEGV rather than
  returning errno into an inconsistent old image. This also covers failures
  after the image switch and in post-install descriptor/signal/procfs work.
- Invalid `PT_LOAD` sizes/address ranges/alignment no longer execute silently.
  They follow the Linux fatal-load contract; a guest's ignored/blocked SIGSEGV
  cannot intercept failed-exec termination.

File capabilities and `CLONE_NEWNS` implementation are not added by this work.

## Managed allocations and complexity

- mmap retains its existing `LinuxFile`/`VmaFileMapping` lifetime and updates a
  counter on acquire/release. Exec checks it in O(1), independently of VMA count.
- Pointer and ELF/shebang header probes use stack storage. Empty vectors do
  not allocate a List or pointer buffer. Short argument strings use a 256-byte
  stack probe, without one ArrayPool rent/return per entry; long strings retain
  bounded pooled growth. Owned argument bytes still need to survive exec.
- The already-checked main ELF path skips a duplicate open/header sniff in the
  process loader. Shebang argument rewriting allocates one exactly sized vector
  and copies references, without List growth, LINQ iterators or another ToArray.
- Initial argv/envp pointers are written directly into the final stack buffer;
  no `uint[argc]` or `uint[envc]` scratch arrays. Loader result/header summaries
  are structs. The initial stack backing array remains necessary.
- Exclusive FD tables are not copied on exec. A shared table reserves all
  collection capacity before acquiring file references, so allocation failure
  cannot leak partially copied references. CLOEXEC snapshots use stack storage
  or an ArrayPool buffer, not a fresh List. Signal dispositions are removed
  in-place, and disabled exec logs do not allocate boxed argument arrays.
- `VfsStream.Read(Span<byte>)` reads ELF stack buffers directly, avoiding
  Stream's pooled-array adapter and extra copy.
- The commit-fault callback is null in production. Only tests allocate its
delegate; ordinary exec does not allocate fault/checkpoint objects.

## Verification

- `Fiberish.Tests`: **2110 PASS / 0 FAIL / 1 existing SKIP** (45 added cases).
- Host runner unit tests: **26 PASS**; final CLI Debug build succeeds.
- Independent exec suite: **56 PASS / 0 FAIL / 7 TCONF assertions**;
  three binaries PASS and two PARTIAL. The same Linux binaries have 63 PASS.
  The seven mount-namespace-dependent assertions remain visibly skipped;
  noexec/nosuid behavior is additionally covered by controlled-mount C# tests.
- LTP permissions: **71/71 binaries PASS**. Pipe/FIFO: **18/18 PASS**.
- Focused clone/vfork/exec regression: **12 PASS / 1 SKIP** (`clone09`).
  No failures or timeouts in these regression batches.

Logs and reports: `artifacts/ltp-exec-final/core-test-fixed.log`,
`cli-build-fixed.log`, and `results/{podish-fixed-tmpfs,regression-permissions,
regression-pipe,regression-proc-exec}.json`. Existing dependency/native-build
warnings are not silently suppressed or treated as behavioral test failures.

## Coverage and reproducibility

`Fiberish.Tests/Syscalls/ExecHardeningFollowupTests.cs` covers controlled noexec
and nosuid mounts without depending on unshare, execute-only/DAC/writer
interpreters, both vectors/pointers/filename budgets, split mapping lifetimes,
invalid interpreter format, and fatal invalid loads. Four injected failure
checkpoints cover before FD unshare, before thread teardown, before address
space switching and after installation. Rollback checks include image buffers,
comm, brk, layout, vDSO addresses and CLOEXEC descriptors. Fatal cases ignore
and block SIGSEGV to verify forced termination.

`InodeRefLinkCountTests` additionally verifies writable mapping references
survive cloned/forked/split reference holders. The warmed hot-check test uses
`GC.GetAllocatedBytesForCurrentThread()` and asserts zero allocated bytes for
access checks, identity computation, empty vector reads and exclusive FD unshare.

The added `Podish.Benchmarks --category exec` uses cached typed syscall delegates
and input vectors stored inside the repeatedly mapped ELF fixture. Runtime and
fixture setup are not measured. It reports both full-exec ns/op and managed
B/op, plus separate exclusive-unshare and empty-vector fast paths.

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor' # this host has net11, not net10
dotnet test Fiberish.Tests/Fiberish.Tests.csproj -c Debug --no-restore
dotnet run --project Podish.Benchmarks -c Release -- --category exec --no-latency --sample-ms 100
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-exec-final --manifest tests/ltp/cases-exec.json --output artifacts/ltp-exec-final/results/podish-fixed-tmpfs.json
```

The binary runner intentionally exits nonzero for partial mount coverage;
missing `CLONE_NEWNS` is not turned into a passing test.

## Performance comparison

Controlled comparison: Windows 11, .NET 11.0.0, Release, 24 logical CPUs,
`DOTNET_TieredCompilation=0`, 100-ms samples, nine samples per case after warmup.
The baseline Core source is exported from
`cebeb72cd6021ef99515d6eb7d17cc482d01cfbc`; both variants use the **same benchmark
executable and identical dependency/native DLLs**. Only `Fiberish.Core.dll`
differs, verified by SHA-256. Baseline managed Core is built separately against
those dependency binaries; it does not rebuild the native engine.

| Case | Before median | After median | Managed B/op before -> after |
| --- | ---: | ---: | ---: |
| full static exec, small vectors | 14.62 us | 14.16 us | 13056 -> 11448 |
| full static exec, 128 argv + 128 env | 34.53 us | 30.65 us | 31720 -> 29104 |
| exclusive FD unshare | 41.6 ns | 10.7 ns | 208 -> 0 |
| empty vector read | 30.2 ns | 24.8 ns | 64 -> 0 |
| read 128 short strings | 8.71 us | 7.03 us | 7368 -> 7336 |

Reversing run order confirmed the full-exec results: small 14.93 -> 14.12 us,
large vectors 34.43 -> 30.95 us, with the same allocation counts. Thus these
fixtures show about **3-5% / 10-11% lower time** and **12% / 8% fewer managed bytes**,
respectively. These are local microbenchmarks, not application-wide speedups.

Results and dependency/source hashes: `artifacts/benchmarks/exec-comparison/`
(`baseline.json`, `fixed.json`, `*-repeat.json`, `provenance.json`). Runnable
snapshots are in `exec-baseline-runtime/` and `exec-fixed-runtime/` alongside it.
Earlier diagnostic runs in other directories are not controlled comparisons:
Debug/Release share a native build directory, so switching builds can replace
dependency DLLs. Do not mix those results or JIT-tiering settings with this table.
