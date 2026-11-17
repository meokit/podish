# Block decode benchmark

This measures repeated 32-bit x86 machine-code to `BasicBlock` decoding via
`DecodeBlock`. The default 23-byte, nine-instruction block includes register,
stack, memory-addressing, arithmetic and conditional-branch instructions. Each
call creates a new block; it does not look up a cached block or execute guest
code. The measurement includes instruction fetch, block optimization and pool
allocation. It excludes guest memory setup and pool reset. One batch is warmed
up and excluded; the pool is reset between batches to bound memory use. The
result checks the decoded instruction count and end address on every call.

```sh
cmake -S libfibercpu -B build/decode -DCMAKE_BUILD_TYPE=Release -DFIBERCPU_BUILD_DECODE_BENCHMARK=ON
cmake --build build/decode --target decode_benchmark -j
./build/decode/decode_benchmark 100 mixed
./build/decode/decode_benchmark 100 memory
```

The first optional argument is the number of measured batches (1,000 blocks each;
default 100). The second selects `mixed` (default), `register`, `memory`, or
`prefix`, `fusion`, or `handwritten`. The register, memory, and prefix cases
decode 32 repeated instructions; the two fusion cases exercise generated and
handwritten patterns. All are synthetic hot blocks. Output reports time per
block, decoded instructions/s and input bytes/s. Compare results on the same
machine and build configuration. CMake may need network access to fetch its
normal dependencies on the first build.

To check the specialization index against the original ordered linear lookup:

```sh
cmake -S libfibercpu -B build/decode -DCMAKE_BUILD_TYPE=Release -DFIBERCPU_TEST_SPECIALIZATION_INDEX=ON
cmake --build build/decode --target specialization_index_test -j
./build/decode/specialization_index_test
```

The index is built once after handler registration. It uses an opcode-indexed
array and a direct key for ModRM presence/value and no-flags state. Each bucket
keeps candidates in the original score order; prefix and memory-shape masks
are still checked on lookup. Memory-shape masks are packed once at registration
into a compact mask and value. This uses additional startup work and memory to
avoid hashing and repeated scans in the decoder. The equivalence test covers
every opcode and ModRM value, both flag states, seeded prefix/memory shapes,
and randomized criteria including out-of-range mask bits; it is not exhaustive
over all memory descriptors.
