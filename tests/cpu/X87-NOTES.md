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

## Deliberately unresolved exact-result cases

The existing finite transcendental implementation calls binary64 `tan`, `atan2`,
`sin`, and `cos`, then converts back to binary80. It cannot retain the extra 11
significand bits expected by hardware x87 tests. Replacing this requires a reviewed
extended-precision implementation and x87 range/exception handling, not looser
assertions or copying corpus outputs into the emulator.

Exact affected IDs from the initial replay:

- `X87/D9_F2` — FPTAN
- `X87/D9_F3` — FPATAN
- `X87/D9_FB` — FSINCOS
- `X87/D9_FE` — FSIN
- `X87/D9_FF` — FCOS

`X87/D9_F9` is different: it supplies `ST(0)=2`, `ST(1)=15` to FYL2XP1.
Intel documents its ST(0) range as approximately
`[-(1-sqrt(2)/2), +(1-sqrt(2)/2)]` (about +/-0.292893); **2 is outside that range**, for which results are
undefined. The expected `8` is not the mathematical `15*log2(3)` (~23.7744).
This test remains visible and bit-exact, but its expectation should not be used
as a general mathematical specification or patched into the implementation.

See Intel SDM instruction references for FPREM/FPREM1, FSCALE, FISTTP, and FYL2XP1;
the online instruction index at <https://www.felixcloutier.com/x86/> mirrors those
descriptions. No new third-party numerical code was imported.
