# Flags Cache Model

## Summary

`flags_cache` is the canonical flags representation while execution remains inside a chained block path.
Repo code must treat `state->ctx.flags_state` as the only persisted flags state, and treat architectural
`EFLAGS` as a materialized view of that state.

The intended model is:

- Chain-local execution reads and writes only the `uint64_t flags_cache` parameter.
- `state->ctx.flags_state` is updated only at explicit commit points.
- Architectural `EFLAGS` are exposed only through accessors.

## Canonical State

- `Context::flags_state` stores the canonical flags state.
- The low 32 bits are the architectural `EFLAGS` image.
- Higher bits store lazy metadata, currently only parity state.
- `GetArchitecturalEflags()` must materialize lazy parity before exposing an architectural view.

Access is centralized through:

- `GetStateFlagsCache`
- `SetStateFlagsCache`
- `GetArchitecturalEflags`
- `SetArchitecturalEflags`
- `CommitFlagsCache`

Op implementations must not read or write flags through `EmuState` directly.

## Access Rules

- Hot-path producers update `flags_cache` only.
- Hot-path consumers read `flags_cache` only.
- Any consumer of `PF` must materialize parity first through `RequirePF()` / `ReadPF()`.
- Direct tests like `GetFlags32(flags_cache) & PF_MASK` are not allowed in op logic.
- Non-lazy flag bits such as `CF/ZF/SF/OF/DF` may be read from `GetFlags32(flags_cache)` when no parity
  materialization is required.

Architectural consumers are the only places allowed to force a commit or reload:

- `pushf/popf`
- `lahf/sahf`
- external API accessors
- fault / interrupt / hook visibility boundaries

## Commit Semantics

`CommitFlagsCache` is only valid at chain exit boundaries:

- `ExitOnCurrentEIP`
- `ExitOnNextEIP`
- `ExitWithoutSyncEIP`
- restart / retry exits
- resolver miss / limit exits
- external visibility points

Successful chaining must not commit. `X86_Run()` and `X86_Step()` must not re-commit a stale caller-side
copy of `flags_cache` after the handler chain returns.

## Review Checklist

When touching flags-related code, verify:

- no op implementation accesses `ctx.eflags`
- no PF consumer uses `GetFlags32(flags_cache) & PF_MASK`
- any architectural export path uses `GetArchitecturalEflags()`
- any architectural import path uses `SetArchitecturalEflags()` / `InitFlagsCache()`
- new chain exit paths call `CommitFlagsCache` exactly once
- any direct state read of flags would fail to compile or be caught in review
