# Podish Swift App Long-Term Roadmap

## Document Status
- Owner: Podish / Fiberish maintainers
- Last Updated: 2026-03-04
- Scope: macOS + iOS app architecture and delivery plan

## Goals
- Build a production-grade Swift app (`Podish`) that manages and runs containers via `Podish.Core`, on top of the `Fiberish` runtime layer.
- Keep runtime semantics aligned with Linux container expectations while preserving cross-platform behavior.
- Expose a stable C ABI from .NET so Swift can drive the full container lifecycle.
- Support concurrent multi-container operation safely in one app process.

## Non-Goals (for now)
- Running host Linux binaries directly on iOS.
- Replacing Core runtime semantics with Swift-only implementations.
- Building a remote orchestration platform.

## Current Baseline
- `Podish` app uses SwiftTerm for terminal rendering.
- `Podish.Core.Native` exists and is built as a static native artifact path.
- A usable native C ABI is already exported from `Podish.Core.Native` and consumed by the Swift app.
- Current native surface already covers:
  - context create / destroy / last-error
  - image pull / list / remove
  - container create / open / start / stop / remove / inspect / list
  - terminal attach / read / write / resize / close
  - log polling and MsgPack event polling
- AOT trim warning convergence was completed.
- Runtime isolation work has started and includes:
  - per-context logging scope
  - runtime instance-scoped memory objects
  - factory-based filesystem registration
  - thread-local native API last-error path
- VFS/page-cache behavior is actively being refined with regression coverage.

## Architecture Principles
- Keep `Fiberish` as the runtime and Linux-semantics layer.
- Keep `Podish.Core` as the container/product layer above `Fiberish`.
- Keep Swift as orchestration + UI.
- C ABI must be explicit, versioned, and backwards-compatible within a major version.
- No global mutable singleton state in runtime paths used by multiple containers.
- Terminal device lifecycle belongs to container runtime, not UI view lifecycle.

## Target Runtime Model (Swift + Core)
- `App Session`
  - owns multiple `ContainerSession`
- `ContainerSession`
  - owns one runtime context
  - optionally owns one guest terminal endpoint (TTY mode)
- `SwiftTermView` (one or more)
  - attaches/detaches to terminal endpoint
  - never owns container lifetime

## Terminal Lifecycle Contract (SwiftTerm, no host PTY)
- `start(tty=true)` creates guest terminal endpoint.
- UI attach opens output subscription; UI detach only removes subscription.
- Container exit triggers terminal drain and terminal close.
- `Ctrl-C`/`Ctrl-Z` are forwarded as guest foreground process-group signals.
- Window resize updates guest terminal size and emits SIGWINCH-equivalent behavior.
- Terminal close must be idempotent.

## C ABI Strategy

### ABI Versioning
- Current exported ABI uses the `pod_*` symbol family in `podish.h`.
- `podish_api_version()` style version negotiation is still a future hardening item and is not exported yet.
- Keep opaque handles for all runtime objects.

### Core Handle Types
- Current ABI uses opaque `void*` handles for context, container, and terminal objects.
- A distinct subscription handle type is not exposed yet.

### Core API Groups
- Runtime
  - create / destroy / configure logging / configure store paths
- Image
  - current ABI: pull / list / remove
  - still missing as direct native entrypoints: load / save / import / export
- Container
  - current ABI: create / open / start / stop / inspect / remove / list
  - still missing as direct native entrypoints: kill / wait
- Terminal
  - current ABI: attach / read / write / resize / close
  - detach is modeled by closing the terminal handle
- Events
  - current ABI: MsgPack polling via `pod_ctx_call_msgpack`
  - callback-style subscriptions are not exposed yet
- Error/Diagnostics
  - thread-local last-error query
  - structured error code + message

### Concurrency Rules
- APIs are thread-safe unless explicitly marked single-thread-affine.
- Each handle has refcounted lifetime rules.
- Subscription callbacks must never re-enter Core with internal locks held.

## Milestone Plan

### M1: Runtime and ABI Foundation
- Status: largely complete.
- Delivered:
  - Swift can create/destroy runtime context
  - Swift can create/open/start/stop/remove containers
  - Swift can inspect/list containers and read last-error state
- Remaining work:
  - explicit ABI versioning
  - contract tests for ABI compatibility and lifecycle guarantees

### M2: Interactive Terminal MVP
- Status: largely complete.
- Delivered:
  - SwiftTerm input/output is connected through terminal attach/read/write
  - resize is wired through the native terminal API
  - terminal lifecycle is decoupled from container creation
- Remaining work:
  - stronger lifecycle tests
  - signal/control-key behavior validation and polish

### M3: Image and Storage UX
- Status: partially complete.
- Delivered:
  - image pull/list/remove wiring exists in native API and Swift
  - logs/event polling path exists
- Remaining work:
  - direct native load/save/import/export entrypoints
  - progress + cancellation plumbing
  - fuller library/history UX in Swift

### M4: Multi-Container and Observability
- Run multiple containers concurrently from Swift.
- Add logs/events screens with filtering and backpressure handling.
- Validate shared-store locking behavior under concurrent operations.
- Deliverable: reliable concurrent workflows.

### M5: iOS Readiness
- Audit APIs for iOS constraints (backgrounding, sandbox, file access).
- Replace macOS-only assumptions in host integration paths.
- Build test matrix for iOS simulators/devices where possible.
- Deliverable: iOS-compatible core interaction surface.

## Test Strategy
- Unit tests
  - handle lifetime/refcount
  - thread-safety of public C API
  - terminal attach/detach semantics
- Integration tests
  - run + exec + wait path from C ABI
  - interactive terminal signal behavior
  - concurrent container start/stop and image operations
- Regression suites
  - VFS/page-cache flush semantics
  - pull/store/import/export correctness

## Risks and Mitigations
- Risk: ABI churn blocks Swift progress
  - Mitigation: versioned API + additive evolution policy
- Risk: callback reentrancy deadlocks
  - Mitigation: strict callback threading contract + lock hierarchy
- Risk: terminal lifecycle leaks
  - Mitigation: idempotent close + ownership graph tests
- Risk: storage corruption under concurrency
  - Mitigation: cross-process file locks + atomic write patterns + crash recovery tests

## Tracking and Delivery Cadence
- Keep this roadmap updated per milestone completion.
- Each milestone should end with:
  - architecture note updates
  - test evidence
  - one demo scenario runnable from clean checkout

## Immediate Next Actions
- Add ABI compatibility/versioning strategy on top of the existing `pod_*` exports.
- Expand native API coverage for image archive flows (`load` / `save` / `import` / `export`).
- Add stronger native API and container-terminal lifecycle tests.
