# Podish.Benchmarks

Microbenchmarks for the guest-runtime hot paths touched by the LTP compatibility
branches. The harness is intentionally dependency-free: it does not use
BenchmarkDotNet, so it builds and runs offline with the rest of the repository.

## Running

```bash
dotnet run --project Podish.Benchmarks -c Release -- --list
dotnet run --project Podish.Benchmarks -c Release -- --category inotify
dotnet run --project Podish.Benchmarks -c Release -- --sample-ms 50
dotnet run --project Podish.Benchmarks -c Release -- --filter mmap --no-latency
```

Results are printed as Markdown and written to
`artifacts/benchmarks/<UTC timestamp>/results.json` by default. Pass `--json`
to choose the path. `artifacts/` is git-ignored.

## Methodology

Throughput cases:

- one scope is created before measurement and reused for every sample;
- at least 150 ms of warmup, then a batch size is calibrated so one sample is
  at least `--sample-ms` long (default 30 ms);
- the median and p95 of 5-9 samples are reported;
- allocated bytes are measured with `GC.GetAllocatedBytesForCurrentThread()`
  around the whole batch and divided by the number of completed operations.

Latency cases:

- each sample performs one operation and returns elapsed microseconds;
- 12 warmup operations, then 80-120 samples; p50/p95/p99/mean/max are reported.

The harness runs benchmarks in one process. It is intended for relative
comparisons and regression tracking, not for absolute hardware calibration.
Close other CPU-heavy applications when collecting a baseline.

## Cases

| Category | What it covers |
| --- | --- |
| `inotify` | Raise with no watch (fast path), one watch, coalescing, 16-watch fanout, watch add/remove, same-watch update, per-inode finalization scopes, parent+inode child events |
| `vfs` | open/close with and without a watch, path walk |
| `scheduler` | wake publication without a waiter, wake-to-run latency, Windows high-resolution sleep overshoot |
| `cpu` | O(1) process CPU snapshot with 1 and 16 threads, including a running task's live delta |
| `mmap` | anonymous and file-backed map/unmap, fixed and auto-selected addresses, dense VMA list |

## Adding a case

Add a `ThroughputBenchmark<TScope>` or `LatencyBenchmark<TScope>` to one of the
category classes. `Run(scope, iterations)` must return the number of operations
completed, so a case can measure a multi-step cycle (for example open+close) as
one operation.

Use `GuestBenchmarkScope` for a minimal guest runtime: one `Engine`, `VMAManager`,
`SyscallManager`, `Process`, `KernelScheduler` and `FiberTask`, with a tmpfs root
and four scratch pages at `0x10000`.
