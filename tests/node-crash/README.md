# Node.js crash reproducer (Alpine 3.12 x86 / Node.js 14.5.0)

## Regression gate (current)

`run-repro.ps1` now resolves the CLI from its own worktree (or an explicit `-Cli`)
and exits 0 only when **every** run exits 0 and prints `[repro] done`. Logs and a
JSON report are retained under `artifacts/verification/node-*`. Run the documented
parameter sets on both `-FsBackend tmpfs` and `-FsBackend silkfs`.

The failure examples below describe the historical defect. Add `-ExpectCrash`
only when intentionally reproducing that defect; never use it as a regression gate.

`powershell -NoProfile -ExecutionPolicy Bypass -File tests/node-crash/test-harness.ps1`
checks the host harness with a separate fake .NET CLI: marker+exit0 passes;
marker+nonzero, missing marker and timeout fail. It also checks explicit historical
crash mode and a CLI path containing spaces. These are harness tests, not guest
Node results. The fake CLI stays under `%LOCALAPPDATA%\Temp\opencode`.

A stable reproducer for the Node.js crash seen when a moderately heavy Node
program runs inside Podish.

## Symptom

The guest process dies with **SIGSEGV** while the allocator is growing the
program break, or hangs in the same state. From the host:

```
[Podish.Cli] ... node /app/repro.js 6 3000 32768
HOST_EXIT=139            # 128 + SIGSEGV
```

Podish's own fault diagnostics show the shape of the failure:

```
[WARN]  Fiberish.Memory.VMAManager: No VmArea found for address 0x5f5bfff8
[INFO]  Fiberish.Task.2: Page Fault at 0x5F5BFFF8 (Read) could not be resolved. Posting SIGSEGV.
[INFO]  Fiberish.Task.2: [FaultDiag] EIP=0xF5BC334D -> ELF_LOAD [...] path=/lib/ld-musl-i386.so.1
[INFO]  Fiberish.Task.2: [FaultDiag] Code bytes @EIP: 8B 48 F8 8B 78 FC 0B 48 F0 0B 78 F4 09 CF 75 D0
```

`8B 48 F8` is `mov ecx,[eax-8]` — musl's allocator reading the chunk header
below the program break.

## Why this shape

The reproducer needs three properties, all of which a real Node program has:

1. **A large `PROT_NONE` reservation.** V8 reserves a region early with a
   non-`MAP_FIXED` `mmap` that carries an address hint:

   ```
   mmap2(0x5F5C0000, 0x7F000, PROT_NONE, MAP_PRIVATE|MAP_ANONYMOUS|MAP_NORESERVE, -1, 0)
   ```

2. **A heap that grows towards that reservation.** musl's `malloc` grows the
   break in small steps; the trace shows 2614 `brk` calls climbing from
   `0x59c90000` to `0x5f5c2000`.

3. **A failed break growth at the boundary.** The last `brk` requests
   `0x5f5c2000`, which collides with the `PROT_NONE` reservation, so Podish
   rejects it:

   ```
   [Syscall] brk(5F5C2000, 5F5B9000, ...)
   = 1599873024            # 0x5F5B9000: the old break, i.e. the growth failed
   ```

   The guest then touches the last page below the requested break, which is
   covered by no VMA in Podish, and dies.

The trace also shows the second, structural deviation: **Podish creates one
VMA per `brk` growth instead of extending a single break VMA**, so the heap
ends up split into 2613 separate `HEAP` VMAs:

```
0x5f5b0000-0x5f5b9000 Read, Write Private, Anonymous HEAP
0x5f5c0000-0x5f600000 Read, Write 16418 MMAP2
```

The page `0x5f5b9000-0x5f5c0000` that the guest still believes it owns is
missing from that list, which is exactly the address that faults.

## Reproducing

### 1. Build the guest image

Podish emulates a 32-bit x86 Linux userland, so the guest is the Alpine
**x86** (i686) port with musl; Node.js 14.5.0 (`nodejs-current`) is the newest
Node that Alpine still ships for 32-bit x86. The image has to be built in
Linux because Alpine ships symlinks and POSIX modes that the Windows `tar`
tool cannot represent.

```powershell
wsl -- bash /mnt/c/Users/Jiang/repos/podish/tests/node-crash/build-image.sh
```

The script downloads the Alpine 3.12 x86 minirootfs plus `nodejs-current` and
its shared libraries, installs `repro.js` at `/app/repro.js`, and writes
`node-rootfs.tar`.

> The archive is deliberately created without the `./` prefix. GNU tar stores
> every entry as `./usr/bin/node`, and Podish's OCI layer index builder keeps
> that prefix, so the guest would resolve `/./usr/bin/node` and the image
> would appear to be missing its executables.

### 2. Import the image

Run import from the repository root: the image store is relative to that folder.
Alternatively supply `-RootfsTar <absolute-path>` to the harness, which imports
and runs in the same worktree automatically.

```powershell
cd <repo>
dotnet .\Podish.Cli\bin\Debug\net11.0\Podish.Cli.dll import `
    <absolute-path>\node-rootfs.tar localhost/podish-node:latest
```

### 3. Run the reproducer

```powershell
powershell -ExecutionPolicy Bypass -File <repo>\tests\node-crash\run-repro.ps1 `
    -Repeats 5 -TimeoutSeconds 60
```

Current regression output (one line per attempt, all must succeed):

```
attempt 1: COMPLETED, 3s
...
5 / 5 accepted; report: <repo>\artifacts\verification\node-...\report.json
```

Historical output from before the brk fix (use `-ExpectCrash` only when
intentionally testing an affected version):

```
attempt 1: CRASH (exit 139), 2.9s
attempt 2: HANG (timeout 60s), 60.4s
attempt 3: CRASH (exit 139), 2.6s
...
reproduced 5 / 5 runs
```

The failure mode alternates between an immediate guest `SIGSEGV` and a hang in
the corrupted allocator state; both are the same defect.

### Manual invocation

```powershell
dotnet <repo>\Podish.Cli\bin\Debug\net11.0\Podish.Cli.dll run --rm --init `
    --log-driver none --network private --fs-backend tmpfs `
    localhost/podish-node:latest -- /usr/bin/node /app/repro.js 6 3000 32768
```

## Parameters

`repro.js` takes `rounds`, `objects` and `bufferSize`:

| Parameter | Effect | Notes |
| --- | --- | --- |
| `rounds` | How many allocate/drop cycles | More rounds = higher hit rate, slower |
| `objects` | Objects per round | Smaller values need more rounds |
| `bufferSize` | Bytes per `Buffer` | 16 KiB–64 KiB reach the collision quickly |

`6 3000 32768` is the fastest reliable setting (≈3 s per run, 10/10 runs
failed). `6 5000 16384` is slightly slower but crashes earlier in the first
round. `6 1000 262144` does not reproduce: the large buffers go through
musl's `mmap` path instead of the break, and the break never reaches the
reservation.

## Diagnostics

```
dotnet <repo>\Podish.Cli\bin\Debug\net11.0\Podish.Cli.dll --log-level debug `
    --log-file repro.log run --rm --init --log-driver none --network private `
    --fs-backend tmpfs localhost/podish-node:latest -- /usr/bin/node /app/repro.js 6 3000 32768
```

The debug log contains the fault diagnostics and the full VMA map; add
`--strace` to also capture the guest syscall stream (the `brk` sequence and
the failing `mmap` reservation).

## Root cause (confirmed)

`SysBrk` (`Fiberish.Core/Syscalls/SyscallHandlers.Memory.cs`) expanded the
program break by calling `ProcessAddressSpaceSync.Mmap(...)` **without
`MapFlags.Fixed` / `MapFlags.FixedNoReplace`**:

1. Because `isFixed` was `false`, when `brk` grew into V8's `PROT_NONE`
   reservation, `VMAManager.Mmap` saw `CheckOverlap == true` and quietly
   relocated the `HEAP` mapping via `FindFreeRegion` to a high address
   (`0xf5xxxxxx`) instead of failing.
2. `SysBrk` ignored the returned address, updated `BrkAddr = newBrk`, and
   returned `newBrk` to musl.
3. Accessing the unmapped boundary page triggered `No VmArea found` (`SIGSEGV`)
   or corrupted musl's free-list pointers into an infinite loop (`HANG`).
4. In addition, every `brk` increment inserted a separate `"HEAP"` `VmArea`
   (2613 VMAs in one trace) instead of extending the trailing `"HEAP"` VMA.

Fixing `SysBrk` to enforce `MapFlags.FixedNoReplace` (failing with the old
`BrkAddr` on overlap so musl falls back to `mmap`) and extending contiguous
`"HEAP"` VMAs in-place (`VMAManager.TryExtendHeapVma`) makes `repro.js`
(10/10 runs), `12 x 3000 x 32768`, `12 x 200000 x 64`, and `silkfs` runs
complete cleanly with exit 0.

## Scope

This reproducer is **not** part of the LTP suites: it needs a musl/Alpine
guest image rather than the static glibc LTP binaries, and it exercises
allocator behaviour rather than a single syscall. It is a standalone
workload under `tests/node-crash/`.
