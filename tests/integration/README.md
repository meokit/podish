# Integration Tests

This directory contains end-to-end Linux userland tests for the current `Podish.Cli` runtime.

The flow is:

- build guest test binaries with `zig cc` through CMake
- launch them through `Podish.Cli`
- assert behavior with `pytest` and `pexpect`

## Build integration assets

```bash
cmake -S tests/integration -B build/integration-assets -DFIBERISH_PROJECT_ROOT=$PWD
cmake --build build/integration-assets --target integration-tests-build
```

Built guest assets are emitted under:

```text
build/integration-assets/assets/
```

## Run the integration suite

```bash
pytest -m integration tests/integration
```

If your assets live somewhere else:

```bash
FIBERISH_INTEGRATION_ASSETS_DIR=/path/to/assets pytest -m integration tests/integration
```

## Runtime notes

- tests use `dotnet run --project Podish.Cli/Podish.Cli.csproj -- ...`
- some scenarios use OCI images, others use Podman-compatible `--rootfs`
- `podman`, `zig`, `pytest`, and `pexpect` may be needed depending on the case
