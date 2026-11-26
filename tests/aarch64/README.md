# AArch64 guest CPU tests

Run the register-instruction differential test on an AArch64 host with a C++23
compiler. The harness executes each fixture word both on the host CPU and through
the guest interpreter with and without block dispatch, then compares X and V
registers, SP, NZCV, and FPSR. It also
runs every bakeable word through its generated exact-word kernel and checks each
invalid encoding for a native illegal-instruction signal and an interpreter trap.

```sh
tests/aarch64/run-native-differential.sh
```

The corpus contains 267 words, including integer arithmetic, flags, carry and
borrow, XZR/WZR and SP operands, bitfields, shifts, compares and selects, scalar
floating-point conversion and arithmetic, and Advanced SIMD. The test runs 80
states per word with the MT19937-64 seed 194629, mixing random bits with boundary
values and varying the floating-point control register. Of those words, 77 also
exercise baking, and 20 check that dead-NZCV handlers preserve the input flags
and match the other native outputs. Eight separate invalid encodings test trap
handling. The differential runner also relocates identical instruction words to different guest addresses and checks ADR, ADRP, literal loads and BL in bound and baked blocks.

The cases are original to this repository and use its license. Their encodings
were generated with the Apple Clang 21.0.0 integrated assembler. The first full
run used an Apple M5 Max on Darwin 27.2.0. The corpus covers representative
instructions. [Repository license](../../LICENSE.md)

The CMake target `fibercpu_aarch64_test` adds memory, control flow, fault
precision, exclusive monitor, self-modifying code, and fused dispatch tests.
Configure with `-DFIBERCPU_TEST_AARCH64=ON` to register it with CTest. The
`build.sh` and `run.sh` scripts in this directory cover guest ELF startup
and architecture switching separately.
