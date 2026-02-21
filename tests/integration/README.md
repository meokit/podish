# Integration Test Framework

This directory hosts end-to-end Linux userland tests for Fiberish:

- Test binaries are cross-compiled with `zig cc` via CMake target `integration-tests-build`.
- Runtime assertions are done with `pytest` + `pexpect` by spawning `dotnet run` of `Fiberish.Cli`.

## 1. Build Integration Binaries

```bash
cmake -S tests/integration -B build/integration-assets -DFIBERISH_PROJECT_ROOT=$PWD
cmake --build build/integration-assets --target integration-tests-build
```

Built binaries are emitted under:

`build/integration-assets/assets/`

## 2. Run Integration Tests

```bash
pytest -m integration tests/integration
```

If assets are in a custom location:

```bash
FIBERISH_INTEGRATION_ASSETS_DIR=/path/to/assets pytest -m integration tests/integration
```
