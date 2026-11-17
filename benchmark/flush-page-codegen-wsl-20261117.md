# `flush_page`: preserve-all versus tail-dispatched slow path

## Scope

Both experiments were built and checked in independent WSL/ext4 source copies
under `/root/podish-flush-codegen-20771da-f6oiq0/`. At the experiment checkpoint
neither was applied to product sources. **Tail-defer was subsequently applied
to the Windows workspace and its WSL source copy at the maintainer's request**;
preserve-all remains experimental. Their standalone patches and full evidence
are in `artifacts/wsl-flush-codegen-20771da/`:

- `preserve-all.diff`
- `tail-defer.diff`
- `assembly/{control,preserve-all,tail-defer}/`
- `assembly/flow-comparison.json`, binaries, build logs and test reports.

The control is the previous **ABI + decoded-segment candidate**, not the original
unoptimized mainline. Compiler and options match the
[WSL verification](handler-codegen-wsl-20261117.md): Linux Clang 22.1.8, Release,
libc++, ThinLTO, `-O3 -march=native`. Default superopcode identity and feature bits
remain unchanged (64 patterns, feature bits 1, profiling OFF).

## Results: both remove the ordinary MOV-store hit-path frame

The exact linked `OpMov_Store_Eax` paths were followed through their conditional
TLB checks and any direct jumps to a shared epilogue. Counts include the final
tail jump. Helper bodies reached by a CALL or tail jump are **not** included.

| MOV-store metric | Control | `preserve_all` | Tail-defer |
| --- | ---: | ---: | ---: |
| Entry ELF VA | `0x91a40` | `0x91040` | `0x90f50` |
| L1 hit instructions | **38** | **34** | **34** |
| L2 hit instructions | **56** | **51** | **51** |
| L1/L2 hit push/pop instructions | 2 | **0** | **0** |
| L1/L2 hit explicit RSP references | 2 | **0** | **0** |
| Write miss instructions up to slow-handler tail jump | 62 | 58 | **51** |
| Write miss push/pop instructions | 2 | 2 | **0** |
| Write miss explicit RSP references | 6 | 0 | **0** |
| Write miss ordinary CALLs in store handler | 1 | 1 | **0** |
| Exact full store-wrapper symbol size | 327 B | 325 B | **303 B** |
| Full shared-object file size | 1,743,112 B | 1,730,824 B | **1,727,648 B** |

The unmodified MOV-load remains a 33-instruction, stack-free L1-hit path in both
experiments. These are codegen/footprint results, **not performance measurements**.
In particular, fewer L2-path instructions do not establish a runtime win; the
previous force-inline experiment already demonstrated why measuring is necessary.

## Experiment A: `preserve_all`

The only source change is an attribute on `SoftTlb::flush_page`:

```cpp
#if defined(__clang__) && __has_attribute(preserve_all)
    __attribute__((preserve_all))
#endif
    void flush_page(GuestAddr vaddr) { /* unchanged */ }
```

The compiler probe reports `preserve_all = 1` on this target. No forced inlining
or noinline attribute was added; the helper remains genuinely out-of-line.

### What moves off the hit path

Control entry and hit-path exit:

```asm
push rbp
sub  rsp, 10h
; ... TLB checks / store ...
add  rsp, 10h
pop  rbp
jmp  rax
```

The preserve-all version has none of these stack operations on L1 or L2 hits.
Clang now keeps the guest store value in EBX and can keep other threaded state
across the call without using the preserved RBP/frame on the whole function.
The remaining alignment save/restore is **shrink-wrapped into the miss block**:

```asm
; 0x91118, reached only on a write miss
push rax
lea  rdi, [r12+1a0h]
mov  ecx, esi                  ; retain threaded branch argument
mov  esi, eax                  ; faulting guest address
call SoftTlb::flush_page       ; 0x91125 -> 0x592f0
; ... stage pending store ...
mov  esi, ecx
pop  rax
jmp  MemoryOpGeneric<false>@plt
```

The push/pop here provides the call's stack alignment. It is not executed on
either successful TLB-hit path.

### Callee cost is real, not eliminated

The helper itself grows **117 -> 132 bytes**, and its entry/returns now save and
restore the registers it actually clobbers:

```asm
push rsi
push rax
; ... invalidate matching tags; R11 is available as scratch ...
pop  rax
pop  rsi
ret
```

`preserve_all` does not blindly spill every register. It changes the calling
contract, so Clang saves the clobbered RSI/RAX and leaves the other required
registers untouched. This shifts cost to actual calls. It also changes the
contract for **all** `flush_page` callers, including non-handler MMU operations.
That global cold-path cost is not covered by the handler instruction table.

## Experiment B: defer flushing into the existing tail-called memory handler

A direct `musttail` from `Mmu::write_tlb_impl` to a handler is not type-correct:
the helper returns `MemResult<...>` and does not have the handler's seven-argument
`int64_t` signature. The tail edge must remain at the dispatch layer, after the
failed write has staged its pending value/address.

The existing route is already suitable:

```text
store handler miss
  -> stage pending write and invalidate both micro-TLB tags
  -> musttail MemoryOpRetry / MemoryOpRestart
  -> MemoryOpGeneric<false / true>
  -> PerformMemOp
  -> Mmu::write<T, false>
  -> flush_page on the blocking write miss
  -> write_slow (CoW / hook / fault / actual write)
```

**The control already performs another flush in that blocking slow write.** The
experimental change removes only the earlier, redundant flush from nonblocking
instruction writes, rather than adding a second new flush-handler wrapper:

```cpp
template <typename T, bool Store, bool FlushOnMiss = true>
MemResult<...> write_tlb_impl(...);

// On miss, after invalidating the threaded micro-TLB:
if constexpr (FlushOnMiss) tlb.flush_page(addr);

// Mmu::write<T, fail_on_tlb_miss>:
write_tlb_impl<T, true, !fail_on_tlb_miss>(addr, val, utlb);
```

The default remains true for direct TLB users and `try_write_target_tlb`.
Blocking writes and transactional carry-RMW therefore keep immediate flushing.
No EmuState field, runtime pending-flush flag, new LogicFlow value, or extra
fast-path branch was added.

### Actual store-handler tail transition

The final miss block has no normal CALL and no stack access anywhere in the
ordinary store wrapper. It stages the pending value and ends at **`0x9107a`**:

```asm
mov  [r12+8130h], ecx          ; pending address
mov  [r12+8134h], 4            ; width
; ... pending type / EIP / done / invalid micro-TLB tags ...
mov  [r12+8120h], eax          ; pending value
jmp  MemoryOpGeneric<false>@plt
```

The slow handler can still have its own frame: the point is to keep it off
successful stores, not to pretend the entire fault-resolution machinery is
stack-free. The 32-bit blocking case in the actual outlined `PerformMemOp`
retains this order:

```asm
; 0x565e4 / 0x565ee: invalidate both micro tags and zero the addend
call SoftTlb::flush_page               ; 0x565f6 -> 0x592d0
; ... precise EIP setup ...
call Mmu::write_slow<unsigned int>     ; 0x56613 -> 0x5ad10
```

Thus the shared read/write/exec TLB invalidation still precedes any write-side
CoW remap, hooks or fault callback. Both threaded micro tags remain invalidated
immediately at the original miss. There is no intervening guest memory access
between staging the failed write and entering its memory handler.

The design depends on that control-flow invariant. It must not be generalized
to callers that continue guest execution, perform a read, or remap memory after
a failed nonblocking write instead of taking the pending-memory route.

## Correctness checks

All three variants ran the complete quick suite using the same managed test
assembly. There are **3,086 identical outcome/message rows** per variant:
**3,080 pass**, the same six pre-existing x87 failures, zero skips/differences.

Five additional public-native-API probes per variant pass and produce identical
register, EFLAGS, EIP, fault-callback and memory snapshots:

1. Warm a read, fault a MOV store, replace backing in the write callback, then
   read the newly mapped page and verify the old backing is unchanged.
2. INC memory across that remap, checking completion exactly once and the
   restarted pending-write route.
3. PUSH across that remap, checking the restarted write and one ESP update.
4. A dword store crossing two pages, replacing the read-only second page and
   verifying both stored halves, with the second page's old backing unchanged.
5. An unhandled read-only store, checking fault EIP, unchanged memory and flags.

These probes supplement, not replace, the existing SMC and carry-RMW regression
coverage. No production assertion was weakened. The parent WSL test output was
restored to the original control library after the comparisons.

## Assessment

- **Both ideas work for this Linux Clang codegen.** `preserve_all` is the smallest
  source edit and preserves the original invalidation timing.
- **Tail-defer has the cleaner resulting store codegen:** identical hit-path
  instruction counts to preserve-all, a smaller full wrapper/library, and no
  ordinary call or alignment push even in the store wrapper's miss block. It
  also avoids the duplicate pre-slow-handler flush.
- This does **not** remove every frame in every instruction family. Carry-RMW
  still has a separate ordinary slow helper, and complex fused handlers can
  retain other saves/calls. For example, a selected four-op handler retains a
  push/pop pair in both experiments.
- These original experiment results cover Linux codegen/correctness, not an
  end-to-end speedup. Subsequent tail-defer application/validation is recorded
  below. Preserve-all and ARM codegen remain outside that application.

## Application checkpoint (incremental performance comparison pending)

The selected tail-defer change is now in `libfibercpu/src/mem/mmu.h` and
`libfibercpu/src/mem/mmu_impl.h`, alongside the retained preserve-none probe and
decoded-segment changes. The working source was synchronized to the WSL ext4
copy: **1,998 files matched byte-for-byte** at the build checkpoint.

- Windows and WSL Release CLI builds succeeded. Both still report feature bits
  **1** and the same default superopcode hash.
- On each platform, the full CPU quick-suite control/candidate comparison has
  **3,091 identical outcome/message rows**: 3,085 pass and the same six known
  x87 failures. Each platform also passes **220/220** selected managed tests.
- Five permanent `DeferredWriteTlbMissTests` cases now cover the remap/restart/
  cross-page/rejected-write scenarios in the normal test project; they pass
  against both the control and the applied candidate. The decoded-segment
  tests are also included.
- The synchronized Windows MOV-store L1-hit path is **41 -> 37 instructions**,
  with no remaining stack operations or ordinary cold call in that wrapper.
  WSL remains **38 -> 34**, with the same stack-free result.
- Only tail-defer was selected. Neither forced inlining nor preserve-all is
  included in the applied product changes.

Fresh incremental Webpack/CoreMark comparisons are running sequentially on
Windows and WSL against the frozen **ABI + decoded-segment, pre-tail** control.
CoreMark uses the same static i386 ELF and 60,000 iterations, three alternating
rounds per platform; Webpack uses Node 14/tmpfs and ABBA order, two samples per
variant/platform, with full native-oracle result checking. Do not attribute the
earlier ABI/segment Webpack improvement to this smaller tail-defer change.

Those performance results are **not complete at this commit checkpoint**. Full
samples, binary identities, source synchronization, assembly and regression
evidence are saved under `artifacts/tail-flush-perf-20771da/`; final timing
conclusions will be reported separately. No ARM performance claim is made.
