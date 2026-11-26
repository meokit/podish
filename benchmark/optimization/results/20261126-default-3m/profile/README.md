# Where the default 3 MiB interpreters spend time

On an Apple M5 Max, AArch64 consistently finishes SHA-256 and gzip compression sooner. i386 consistently finishes GNU diff sooner. The other apparent i386 wins depend on which commands run earlier in the process. Recursive ls showed the largest such change. GNU grep provides a second example with a measurable difference inside its DFA loop.

## Uninstrumented elapsed time

Both guests used the same packaged Release CLI and arm64 dylib as the [original run](../README.md). Timings are median milliseconds per command. Full repeats the original 17 cases in order for 12 guest pairs. Short runs gzip, sha256, ls, grep-regex and diff-revisions in that order for 15 pairs. Each guest gets a fresh CLI per round. The starting guest alternates. Every command group passed its output digest. The ratio divides the i386 median by the AArch64 median. The win column counts individual pairs and can differ from the ordering of the two medians.

| Case | Full i386 ms | Full A64 ms | Ratio | Full wins, i386 : A64 | Short i386 ms | Short A64 ms | Short wins, i386 : A64 |
|---|---:|---:|---:|---:|---:|---:|---:|
| sha256 | 104.41 | 29.32 | 3.561 | 0 : 12 | 101.72 | 29.78 | 0 : 15 |
| gzip | 271.23 | 191.26 | 1.418 | 0 : 12 | 267.14 | 188.46 | 0 : 15 |
| diff-revisions | 36.69 | 39.44 | 0.930 | 12 : 0 | 41.93 | 45.86 | 15 : 0 |
| ls | 18.63 | 24.51 | 0.760 | 6 : 6 | 23.05 | 18.65 | 0 : 15 |
| grep-regex | 25.51 | 26.27 | 0.971 | 11 : 1 | 27.21 | 26.35 | 1 : 14 |

## SHA-256

The input contains 2,097,204 bytes. Both sha256sum ELFs implement software rounds in sha256_process_block. Expanding the endian-conversion inner loop over one steady-state 64-byte compression block gives these counts.

| Per 64 bytes | i386 | AArch64 |
|---|---:|---:|
| Guest instructions | 3,933 | 1,919 |
| Guest instructions referencing memory | 819 | 51 |
| Of these, explicitly stack-relative | 779 | 39 |
| Standalone rotates | 576 | 224 |
| EOR with a rotated source | 0 | 352 |

The i386 loop starts at ELF 0x3410 with sixteen trips through 0x3430–0x3440. AArch64 starts at 0x39b0 with four trips through 0x39bc–0x39d0. Memory counts exclude address arithmetic and count each pair or vector instruction once. AArch64 also converts input with vector REV32. Neither ELF uses hardware SHA rounds here.

In the i386 interpreter, MOV handlers accounted for 2,010/5,241 native worker leaves. A representative stack read at host 0x7a74c performs the page checks, loads the guest word, updates a guest register and tail-dispatches. On AArch64, three- and four-word handlers accounted for 2,972/5,035 native leaves. At guest 0x4588 the four instructions ROR, ORR, ADD and EOR-with-ROR share one tail dispatch at host 0x3f1c5c. The emitted code still uses shift/bitfield pairs for the rotations in this handler.

Replaying the shipping 3 MiB table against the archived command predicts 112,713,090 dispatches over 129,354,170 i386 guest slots and 19,563,104 over 63,242,914 AArch64 guest words. Multi-op coverage is 19.2% and 95.9%, respectively. i386 weights block entries and can count unexecuted suffixes after internal exits. AArch64 weights retired sequences. These are model estimates. The guest disassembly and independently sampled handler mix both point in the same direction.

## gzip compression

The two gzip 1.12 ELFs search the same predecessor chain in longest_match. The captures record 6,118,517 entries at i386 ELF 0x3470 and 6,118,515 at AArch64 ELF 0x3890. Both interpreters fuse the chain lookup, comparison and branch into one handler. The sampled symbols account for 438/5,334 i386 and 374/5,291 AArch64 native worker leaves.

After loading the chain entry, i386 compares it with a limit stored at guest [%esp]. Host 0x1fa964–0x1fa97c checks the guest page boundary and cached page and loads that stack word. AArch64 keeps the limit in guest w8, so host 0x450a18 reads it directly from CPU state. From the flag-producing SUBS through the conditional branch inclusive, the i386 handler emits 21 host instructions to form EFLAGS and consume CF. AArch64 emits three, SUBS, MRS NZCV and TBNZ. A second candidate-byte check similarly gets its comparison byte from the i386 guest stack and from an AArch64 register. Both differences recur in the hot loop. Their individual shares of elapsed time have not been measured.

## diff-revisions

The held-out utility's two ELFs spend approximately 3.84 million iterations in find_and_hash_each_line at src/io.c:340. The output was byte-identical (53,290 bytes). The i386 loop occupies six instructions at ELF 0xa5c0, with 3,747,648 entries there and another 89,630 from 0xa5ba. The AArch64 loop occupies five instructions at 0xb1b0, with 3,837,274 complete captured iterations. Neither side selects a multi-op handler for the inner loop.

| Operation | i386 host | AArch64 host |
|---|---|---|
| Read byte and advance pointer | Specialized MOVZX at 0xfdc44, 12 instructions on first-page hit. Exact pointer increment at 0xc2d88 adds 5. | Post-index LDRB at 0x2869c takes 77 instructions on the corresponding hit path. It unpacks dynamic registers, chooses the addressing mode, width and extension, and handles base writeback. |
| Rotate | Exact ROL at 0xf423c emits a single immediate host ROR between the guest register load and store. | Generic EXTR at 0x26c30 handles both sources and a runtime rotation amount. |
| Add current byte | Exact register ADD still has to generate x86 flags. | Extended ADD at 0x1fcd4 selects extension width and signedness before shifting and adding. |

Counts include the tail jumps on the stated paths, two jumps for the i386 load plus increment. The AArch64 generic load, extended add and extract handlers account for 1,154, 528 and 268 out of 5,296 worker samples. The i386 MOVZX handler accounts for 171/5,282. The i386 conditional jump and byte compare also cost 621 and 455 worker samples. This explains how AArch64 can execute fewer guest instructions yet lose on this input. Binding post-index byte-load mode, direction and width at decode time is a narrow A/B candidate that can keep the registers and displacement dynamic.

## Recursive ls

Both ELFs hit the GNU filename quoting loop at the same frequency. The archived captures show 15,283 visits to i386 ELF 0x14238 and AArch64 ELF 0x1383c inside quotearg_buffer_restyled. The larger sampled cost is in the host directory path. In a separate trace of a single command each ls made 58 getdents64 calls (60 including shell glob expansion). The repeated-command worker stacks on both sides reach SysGetdents64 → HostInode.GetEntries → TryProbeUnixNode → TryReadUnixStat → lstat_darwin → Darwin lstat.

| Worker profile | i386 (4,991 ticks) | AArch64 (4,895 ticks) |
|---|---:|---:|
| Any guest syscall subtree | 2,678 (53.7%) | 3,220 (65.8%) |
| Attributed to SysGetdents64 | 2,120 (42.5%) | 2,634 (53.8%) |
| Host lstat self | 1,415 (28.4%) | 1,722 (35.2%) |

The percentages overlap and each guest has its own denominator. In the full panel, per-round i386 timings cluster around 18.6 and 28 ms, and AArch64 around 17 and 24–26 ms. The full-panel ratio of medians is 0.760 while the median paired ratio is 0.908. Neither supports a repeatable 24% i386 advantage.

A separate six-pair control preserved the full seven-workload prefix before ls and measured three consecutive batches of 6, 6 and 36 ls invocations. Modes and guest order were counterbalanced across rounds. The first six under default host .NET tiering took 26.92/24.44 ms (i386/AArch64), with AArch64 ahead in four pairs. Changing only DOTNET_TieredCompilation to 0 gave 17.18/14.09 ms, with AArch64 ahead in all six. In the final 36-invocation batch the default medians were 18.47/23.80 ms and disabling tiering gave 16.57/13.00 ms, again with AArch64 ahead in all six under the control. Host tiering materially changes the outcome on this fixture. The experiment does not isolate a particular tier transition or guest handler.

## grep-regex

Both ELFs hit dfaexec_main at lib/dfa.c:3485. The historical guest capture records 1,691,148 visits to the corresponding byte load, indexed state lookup, comparison and branch on each guest. i386 ELF 0x1206b/0x1206d bind to a single baked CMP/JE handler at host 0x20ddf0 (412/4,800 native worker leaves). Its SUBS immediately feeds B.NE. AArch64 ELF 0x11c30/0x11c34 bind to separate handlers. The comparison at host 0x1f660 stores NZCV and tail-dispatches. The branch at 0x32c154 tests Z (551/4,893 native leaves). The indexed loader also accounts for 503/4,893 native leaves. The i386 fusion saves a dispatch in this loop. The complete command's lead changes with panel order.

An eight-pair diagnostic disabled host .NET tiered compilation and ran each prefix through grep-regex, alternating prefix order and guest order between rounds. With the full prefix, median i386/AArch64 times were 25.00/24.29 ms and AArch64 won seven pairs. With the short prefix they were 28.19/27.48 ms and AArch64 won all eight. The original full-panel i386 lead disappears in this control even though the native guest loop uses the same baked handlers.

## Reproducing the evidence

The [rounds and profile counts](observations.json) include the raw elapsed times, tiering controls, syscall counts, duration of the sampled command loops, output digests and most frequent worker symbols. The [paired disassemblies](disassembly.md) use the actual Debian guest ELFs and the packaged libfibercpu.dylib (Mach-O UUID 1AA319C0-2ED1-3FAA-A3E8-3CD11430248E). Guest debug symbols were matched by GNU build ID. The original [measurement archive](../measurements.json) identifies the native and managed binaries, fixtures, command lines, repetitions and canonicalizers.

For timing, use the parent report's run_workloads invocation with native_defaults=True and the case orders and round counts above. For each macOS profile, start the same CLI with DOTNET_PerfMapEnabled=3, run the shell command once to warm up, signal the sampler and repeat the command in the same guest shell for the number of repetitions recorded in observations.json. Redirect output inside the guest and verify its canonical SHA-256. Run the following on the host after the warmup marker. The guest loop must still be running when sampling finishes.

```sh
sample "$cli_pid" 7 1 -file "$guest-$case.sample"
cp "/tmp/perf-$cli_pid.map" "$guest-$case.perfmap"
llvm-objdump -d --no-show-raw-insn --start-address=0x2869c --stop-address=0x289b0 "$cli_dir/libfibercpu.dylib"
```

Only the .NET Long Task guest execution thread is counted. Self ticks subtract direct-child ticks in the stack tree, including rows listing multiple PCs for one symbol. Managed frames are resolved with the matching process perf map. Native handler shares are relative to native worker leaves unless a whole-worker denominator is given explicitly. Each symbol aggregates all of its guest use sites. Host instruction lengths count static instructions along a specified path. Neither measure assigns milliseconds to individual instructions.
