# Flags cache

The interpreter keeps guest flags in the `uint64_t flags_cache` argument while handlers tail-call each other. `X86_Run` and `X86_Step` load that argument from `Context::flags_state` when they enter a handler. The handler that returns to the caller commits its copy.

## Representation and accessors

Bits 0–31 carry the EFLAGS image with a deferred parity flag. Bits 40–47 hold the byte that determines `PF`. Arithmetic handlers can store the low byte of their result there. When the caller supplies a parity bit, `InitFlagsCache` and `SyncParityStateFromFlags` encode it as 0 for set or 1 for clear. `EvaluatePF` returns the even parity of the stored byte in either case.

Use these helpers when editing an instruction or exposing flags to a caller.

| Task | Helper |
| --- | --- |
| Read `CF`, `ZF`, `SF`, `OF`, or `AF` | Use `ReadCF`, `ReadZF`, `ReadSF`, `ReadOF`, or `ReadAF`. These helpers emit per-flag reads in LLVM templates. |
| Read `PF` | Use `EvaluatePF`. `GetFlags32` and `GetFlags32NoPF` both return the raw low word, which can contain a stale `PF` bit. |
| Read or replace persisted EFLAGS | Use `GetArchitecturalEflags` to materialize `PF` and `SetArchitecturalEflags` to initialize the parity state. The native EFLAGS APIs call these helpers. |
| Persist the threaded value | Use `CommitFlagsCache`. It stores the entire word, including deferred parity. |

The helpers live in [exec_utils.h](../libfibercpu/src/exec_utils.h).

## Handler exits and guest instructions

Handlers pass the cache through successful links, including links that the slow resolver finds in the block cache. The exit handler commits it when the instruction budget expires, no successor is available, or an instruction returns control with its current or next EIP. A memory replay that stops the chain also commits. Successful replay passes the cache to the restarted or next instruction. See [dispatch.h](../libfibercpu/src/dispatch.h) and [ops.cpp](../libfibercpu/src/ops.cpp).

`PUSHF` commits before reading architectural EFLAGS. `POPF` applies the architectural write mask and updates both the persisted state and the local cache. `LAHF` and `SAHF` handle parity through the local cache. `INT n` commits before invoking the interrupt hook and reloads the cache after it returns, because the hook can replace the guest state. See [ops_control_impl.h](../libfibercpu/src/ops/ops_control_impl.h) and [ops_data_mov_impl.h](../libfibercpu/src/ops/ops_data_mov_impl.h).

The carry-dependent memory forms of `ADC`, `SBB`, `RCL`, and `RCR` preserve their input flags when the slow store fails. The slow handler publishes the input before the store and stores the candidate flags on success. Its caller reloads the cache from state. See [ops_carry_rmw_impl.h](../libfibercpu/src/ops/ops_carry_rmw_impl.h) and [CarryRmwSmcTests.cs](../Fiberish.X86.Tests/Regression/CarryRmwSmcTests.cs).

## LLVM block and page-region compilation

After inlining the instruction kernels, the compiler lowers the symbolic flag reads and writes in `DeferredFlags`. It tracks the arithmetic flag fields and parity byte as separate SSA values, including across control-flow joins. A per-flag read uses its field directly. Uses that require the handler ABI word receive a packed value.

When you enable `FIBERCPU_LLVM_SHARED_REGION_EXITS`, the region builder joins compatible exits so the compiler can pack flags at their shared ABI boundary. See [llvm_operand_ir.cpp](../libfibercpu/src/jit/llvm_operand_ir.cpp) and [LLVM block JIT options](instruction-specialization.txt).
