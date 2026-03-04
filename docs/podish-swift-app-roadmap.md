# Podish Swift App Long-Term Roadmap

## Document Status
- Owner: Podish / Fiberish maintainers
- Last Updated: 2026-03-04
- Scope: macOS + iOS app architecture and delivery plan

## Goals
- Build a production-grade Swift app (`Podish`) that manages and runs containers via `Podish.Core`.
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
- AOT trim warning convergence was completed.
- Runtime isolation work has started and includes:
  - per-context logging scope
  - runtime instance-scoped memory objects
  - factory-based filesystem registration
  - thread-local native API last-error path
- VFS/page-cache behavior is actively being refined with regression coverage.

## Architecture Principles
- Keep container/runtime logic in Core; keep Swift as orchestration + UI.
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
- Export `podish_api_version()` and enforce min/max compatibility checks in Swift bootstrap.
- Keep opaque handles for all runtime objects.

### Core Handle Types
- `podish_runtime_t`
- `podish_container_t`
- `podish_terminal_t`
- `podish_subscription_t`

### Core API Groups
- Runtime
  - create / destroy / configure logging / configure store paths
- Image
  - pull / load / save / import / export
- Container
  - create / start / stop / kill / wait / inspect / remove
- Terminal
  - open / attach / detach / write / resize / close
- Events
  - subscribe to container and runtime events
- Error/Diagnostics
  - thread-local last-error query
  - structured error code + message

### Concurrency Rules
- APIs are thread-safe unless explicitly marked single-thread-affine.
- Each handle has refcounted lifetime rules.
- Subscription callbacks must never re-enter Core with internal locks held.

## Milestone Plan

### M1: Runtime and ABI Foundation
- Finalize minimal stable C ABI for runtime/container/terminal.
- Remove remaining global mutable state in hot paths.
- Add ABI contract tests (native layer).
- Deliverable: Swift can create runtime and run non-interactive container commands.

### M2: Interactive Terminal MVP
- Connect SwiftTerm input/output to guest terminal endpoint.
- Implement resize and control key signal forwarding.
- Ensure attach/detach without container interruption.
- Deliverable: stable interactive shell session in app.

### M3: Image and Storage UX
- Expose pull/load/save/import/export via C API and Swift UI.
- Add progress + cancellation plumbing.
- Build image/library views and operation history.
- Deliverable: app-native image lifecycle management.

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
- Finalize C ABI draft for M1 (runtime/container/terminal minimal set).
- Implement SwiftTerm input path through terminal write API.
- Add container-terminal lifecycle integration tests in `Fiberish.Tests` and native API tests.
