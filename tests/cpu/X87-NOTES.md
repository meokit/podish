# x87 replay findings

These notes distinguish architectural bugs from limitations of the implementation
or assumptions in the pinned upstream corpus. Original assembly and CONFIG
expectations are unchanged; exact comparisons stay enabled.

## Architectural fixes

- `FLDL2E`: corrected the mistyped binary80 log2(e) constant to
  `3FFF:B8AA3B295C17F0BC` (nearest rounding).
- `FSCALE`: truncates ST(1) toward zero independently of the control-word rounding
  mode, without signaling an inexact integer-conversion exception. The SoftFloat
  path now scales the binary80 exponent directly and uses its ext80 overflow and
  subnormal rounding, rather than creating an overflowing power-of-two intermediate.
- `FPREM` uses the truncating quotient; `FPREM1` uses the nearest-even quotient.
  Both preserve binary80 operands/results instead of converting through binary64.
  FPREM corrects SoftFloat's IEEE remainder by one signed divisor when necessary.
  The preexisting absence of partial-reduction/quotient-condition-code modeling
  is not resolved by this numerical-result fix.
- `FISTTP m64int` (`DD /1`) uses the same checked truncation/pop behavior as the
  existing 16/32-bit forms. **All FISTTP forms are SSE3 additions**, not baseline
  80387 instructions. The directory name `X87` is an upstream categorization, not
  a minimum-CPU-feature declaration.
- Infinite trigonometric operands now propagate masked invalid-operation status
  through SoftFloat. Finite operands with magnitude at least 2^63 set C2 and do
  not push or replace the stack value.
- Legacy `FLDENV`/`FNSTENV` and `FRSTOR`/`FNSAVE` respect the distinct protected-mode
  14/28-byte layouts. Save images contain logical ST0..ST7, not physical R0..R7.
  FIP, FDP, FOP and selectors are saved/restored; FNINIT/FNSAVE clear metadata.
  Environment/control instructions do not replace the last data-instruction FIP.
- FINCSTP/FDECSTP already changed TOP and the logical view correctly; they now also
  clear C1. Their initial corpus replay failures were due to later MMX instructions
  resetting the internal TOP without synchronizing FSW.TOP, not stack rotation.
  MMX entry now synchronizes both TOP views, marks all tags valid and sets the
  written register's high word to `FFFF`; EMMS empties tags without changing TOP.

`RegressionFpuTests` supplies independent small architectural checks for these
behaviors. Final native replay results are recorded in [RESULTS.md](RESULTS.md).

## Extended-precision transcendental evaluation (2026-11-17)

Finite SIN/COS/TAN and ATAN2 now use the existing Berkeley SoftFloat dependency's
binary128 arithmetic for range reduction and convergent series, then convert the
final result to binary80. No corpus outputs are embedded in the evaluator and the
FPATAN/FSINCOS/FSIN/FCOS corpus assertions remain bit-exact. For the FPTAN(1)
hardware sample only, the runner permits a one-unit binary80 significand distance,
with exact sign/exponent and exact pushed 1. The mathematically rounded tan(1)
is `3FFF:C75922E5F71D2DC5`; the corpus hardware sample ends in `DC6`.
Intel SDM vol. 1 section 8.3.10 specifies approximate transcendental results,
including a 0.78 ulp error bound for FPTAN at |x| < 1.25:
[transcendental accuracy](https://cdrdv2-public.intel.com/843827/253665-sdm-vol-1-dec-24.pdf).
An independent regression requires the mathematical result exactly, so the old
binary64 implementation cannot pass via this allowance. Signed-zero,
quadrant and exceptional-operand regression checks accompany the corpus replay.
This is not a claim of hardware-identical results for every input: argument
reduction near the 2^63 limit and all x87 exception/rounding combinations need
further validation.

The missing binary128 entry points and NaN helpers were copied unchanged from
[Berkeley SoftFloat](https://github.com/ucb-bar/berkeley-softfloat-3/tree/07ebd29db0cfb55723a79fbbffe99103ef38e305/source)
(commit `07ebd29db0cfb55723a79fbbffe99103ef38e305`), with the existing 8086-SSE
specialization and BSD license headers. The `s_mul128To256M` buffer primitive is
explicitly included by CMake; it is not a memory-API float operation.

## Undefined upstream FYL2XP1 oracle

`X87/D9_F9` supplies ST(0)=2 and ST(1)=15, but expects 8 rather than the mathematical
15*log2(3). Intel defines the ST(0) range as approximately +/-0.292893; results
outside it are undefined. See the FYL2XP1 entry in the
[Intel instruction reference](https://cdrdv2-public.intel.com/835757/325383-sdm-vol-2abcd.pdf).

This source is explicitly inventoried as blocked with a reason, rather than
executed as a portable exact-result test. Its original source, manifest, binary,
and expected registers are unchanged. The importer reproduces the classification
and an inventory test checks it. No special-cased emulator answer is used for this undefined input.
