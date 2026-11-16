# execve Linux oracles and standalone LTP tests

Date: 2026-11-16. Scope: the five follow-up items in
[EXEC_HARDENING.md](EXEC_HARDENING.md), not runtime repairs.
The subsequent runtime repair and performance report is
[EXEC_FIX_REPORT.md](EXEC_FIX_REPORT.md).

## Linux contracts

| Area | Linux behavior | Binary |
| --- | --- | --- |
| ELF `PT_INTERP` | `open_exec()` checks execute DAC, directory search, noexec and deny-write access. Denied execute/search or noexec returns `EACCES`; an open writer returns `ETXTBSY`, even if its FD is CLOEXEC. Execute-only interpreters are allowed. | `exec_interp01` |
| Mapping-held writers | `mmap()` retains the open **file description**. Closing its descriptor does not release write access while a VMA holds it. This includes `MAP_PRIVATE` and read-only VMAs created from `O_RDWR`, not just writable `MAP_SHARED`. `mprotect(PROT_READ)` does not release the writer; final `munmap()` does. A mapping from `O_RDONLY`, including writable `MAP_PRIVATE`, does not deny exec. | `exec_mmap01` |
| Set-ID | `S_ISUID` changes EUID; SGID changes EGID only if **both** `S_ISGID` and `S_IXGRP` are present. `MS_NOSUID` suppresses both transitions without rejecting ordinary execution on the reference kernel. The main ELF's mount/mode matters, not its `PT_INTERP`'s. | `exec_creds01` |
| Argument budget | argv + envp share `max(128 KiB, min(RLIMIT_STACK/4, 6 MiB))` on this 4-KiB-page Linux reference. Pointer storage, NUL bytes and the copied exec filename also count. Each string separately must fit `MAX_ARG_STRLEN` (128 KiB here). | `exec_args01` |
| Exec failure | Early failure returns errno with the old image usable. After the point of no return Linux does **not** roll back: failed loading terminates the task with `SIGSEGV` (since 3.17). A `PT_LOAD` with `p_filesz > p_memsz` provides a deterministic late failure here. | `exec_commit01` |

The mode macro is `S_ISGID`, not `S_IGSID`.

### Source references (Linux v5.15)

- [`fs/exec.c`](https://kernel.googlesource.com/pub/scm/linux/kernel/git/torvalds/linux/+/v5.15/fs/exec.c):
  `do_open_execat()` / `open_exec()`, `bprm_stack_limits()` /
  `copy_strings()`, `bprm_fill_uid()`, `begin_new_exec()` and `bprm_execve()`.
  The budget subtracts `(argc + envc) * sizeof(void *)` before copying
  strings. On x86_64 this is kernel pointer size even for compat-i386 callers;
  the tests deliberately avoid that ABI-sensitive edge.
- [`fs/binfmt_elf.c`](https://kernel.googlesource.com/pub/scm/linux/kernel/git/torvalds/linux/+/v5.15/fs/binfmt_elf.c):
  `load_elf_binary()` calls `open_exec()` for `PT_INTERP` **before**
  `begin_new_exec()`, but checks the main `PT_LOAD` size constraint afterwards.
- [`mm/mmap.c`](https://kernel.googlesource.com/pub/scm/linux/kernel/git/torvalds/linux/+/v5.15/mm/mmap.c):
  `mmap_region()` takes `get_file(file)` for `vma->vm_file`; `remove_vma()`
  releases it with `fput()`.
- [`fs/file_table.c`](https://kernel.googlesource.com/pub/scm/linux/kernel/git/torvalds/linux/+/v5.15/fs/file_table.c):
  final `__fput()` releases `FMODE_WRITER` with `put_write_access()`.
- [`execve(2)`](https://man7.org/linux/man-pages/man2/execve.2.html):
  permissions, limits, nosuid and the fatal late-failure path. ETXTBSY briefly
  changed in Linux 6.11/6.12; upstream `execve04.c` records the revert in
  6.13-rc1, 6.11.11 and 6.12.2. This oracle is 5.15, not a claim about every
  historical kernel or every LSM policy.

## Standalone binaries

Sources are in `exec/`, outside the pinned upstream submodule. They use the
real LTP harness (`tst_test.h`, TPASS/TFAIL/TBROK/TCONF, summary and exit
bitmask), statically linked into **five separate i386 executables**. No guest
shell, compiler, glibc installation, actual ld.so or helper binary is needed.
Each builds its tiny ELF fixtures itself and observes exec through a CLOEXEC
errno pipe plus `waitpid()`. Replacement payloads exit 42. The credential
payload checks UID/EUID/GID/EGID and `AT_SECURE`, exiting 43 on a mismatch.
Interpreters are position-independent `ET_DYN` ELF images, like normal ld.so.
This avoided masking the original access-check gaps with an unrelated
interpreter-type restriction; the runtime repair also accepts `ET_EXEC`.

`exec_interp01` and `exec_creds01` need root and mount namespaces. They first
unshare a mount namespace and make `/` recursively private, then mount only
disposable tmpfs fixtures. If namespace creation is unavailable, only variants
requiring mount flags report TCONF; ordinary-file DAC/writer/SGID checks still
run. A nosuid scratch mount also skips uncontrolled set-ID checks. They never
fall back to changing host mount flags. DAC/set-ID children
drop supplementary groups and all real/effective/saved IDs to 1000. Native
scratch must be on Linux `/tmp`, not WSL `/mnt/c`.

`exec_args01` changes the stack soft limit only in children. Its 4-KiB strings
are below the per-string limit; failing splits fit **each individual** vector
budget. Empty strings exercise pointer accounting; low-stack controls test
the 128-KiB floor. No boundary depends on libc's inherited environment.

`exec_commit01` checks early invalid-main (`ENOEXEC`) and invalid-interpreter
(`ELIBBAD`) failures. Both must preserve CLOEXEC FDs, signal disposition and
delivery, altstack, comm, anonymous memory and attached SysV shared memory
(including unchanged attachment counts sampled before and after exec).
A separate child tests fatal late loading.

### Commit coverage limit

A guest binary cannot deterministically inject managed scheduler invariant
exceptions in `DetachSiblingThreads()`, SysV detach or address-space rebinding.
Those Podish-only paths need host-side fault injection; early malformed ELF
tests are **not** coverage for those exceptions. The subsequent runtime repair
adds that host-side coverage; see [EXEC_FIX_REPORT.md](EXEC_FIX_REPORT.md).

Linux sets its point of no return before `de_thread()` and `unshare_files()`.
Podish may stage more work, but after irreversible teardown it must complete
or terminate, not return errno into a partially mutated old image. Full
rollback after that boundary is not a Linux requirement. The late ELF case
is intentionally a strict Linux differential check, even if earlier rejection
would be safer.

## Recorded comparison (before runtime repairs)

Reference: WSL Linux `5.15.153.1-microsoft-standard-WSL2`, root, compat-i386,
4-KiB pages. Guest: Windows Podish Debug, tmpfs image backend. The CLI was
rebuilt from the current checkout; no runtime code was changed in this round.
LTP harness revision: `e74f6d6f1c09e6be1aee0a09d347d818be3a36e8`.

| Binary | Linux PASS / FAIL / SKIP | Podish PASS / FAIL / SKIP |
| --- | --- | --- |
| `exec_interp01` | 8 / 0 / 0 | 2 / 4 / 2 |
| `exec_mmap01` | 16 / 0 / 0 | 11 / 5 / 0 |
| `exec_creds01` | 11 / 0 / 0 | 4 / 2 / 5 |
| `exec_args01` | 9 / 0 / 0 | 5 / 4 / 0 |
| `exec_commit01` | 19 / 0 / 0 | 17 / 2 / 0 |
| **Assertions** | **63 / 0 / 0** | **39 / 17 / 7** |

All five binaries pass natively; all five correctly report FAIL on Podish.
There are no BROKEN cases or timeouts. Direct standalone native execution
with `-i 10` also passes: **630 assertions**, no failures or skips. Host runner
unit tests: 26 PASS. CLI Debug build: zero errors.

Observed guest differences:

- PT_INTERP with no execute bits, denied file DAC, or an open writer still
  executes. Denied directory search returns `ENOENT` instead of `EACCES`.
- All five mapping-held-writer variants wrongly execute after descriptor
  close. Final-unmap and O_RDONLY controls pass.
- Both SGID-without-group-execute variants exit 43 (wrong credentials/auxv).
- Three oversized combined string splits and the combined empty-string
  pointer budget wrongly execute. Single-vector and below-limit controls pass.
- Invalid ELF interpreter format returns `ENOEXEC`, not `ELIBBAD`.
  Worse, the invalid `PT_LOAD` executes and exits 42 instead of being killed.
  The old image's state is preserved on the early rejection paths tested.
- `unshare(CLONE_NEWNS)` returns `ENOSYS`: **two noexec and five nosuid-related
  variants are TCONF**, not guest passes. Their Linux behavior is verified,
  but no Podish mount-flag conclusion is inferred from those skips.
- Guest SysV `shm_nattch` is already zero before exec (Linux reports one).
  The preservation test compares before/after counts and live contents; it
  does not misattribute this separate pre-existing IPC discrepancy to exec.

Artifacts for this final source snapshot are under `artifacts/ltp-exec-final/`:
`bin/`, `build.json`, `build.log`, `results/native.json`,
`results/podish-tmpfs.json`, run-local logs in `runs/`, and `repeat-native/`.
The source snapshot hashes were checked against all seven local source files.

## Build and run

The selected-case builder copies `localSuite: exec` into a new directory in
the disposable LTP build tree. No upstream source/assertion is patched.
`build.json` records local source and binary hashes.

```sh
bash tests/ltp/build.sh artifacts/ltp-exec tests/ltp/cases-exec.json
sudo python3 tests/ltp/run.py --backend native \
  --artifacts artifacts/ltp-exec --manifest tests/ltp/cases-exec.json
```

Windows/WSL:

```powershell
wsl -d Ubuntu -u root -- bash /mnt/c/Users/Jiang/repos/podish/tests/ltp/build.sh /mnt/c/Users/Jiang/repos/podish/artifacts/ltp-exec /mnt/c/Users/Jiang/repos/podish/tests/ltp/cases-exec.json
wsl -d Ubuntu -u root -- python3 /mnt/c/Users/Jiang/repos/podish/tests/ltp/run.py --backend native --artifacts /mnt/c/Users/Jiang/repos/podish/artifacts/ltp-exec --manifest /mnt/c/Users/Jiang/repos/podish/tests/ltp/cases-exec.json
python tests/ltp/run.py --backend podish --artifacts artifacts/ltp-exec --manifest tests/ltp/cases-exec.json
```

One binary can also be invoked directly by absolute path from a writable Linux
scratch directory, as root for mount/credential cases. LTP's `-i 10` repeats
assertions. The runner requires a passing native baseline from identical
binary hashes before Podish execution. Known guest failures are not XFAILs.
