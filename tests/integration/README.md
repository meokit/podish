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
- the Wayland e2e flow builds `tests/integration/Containerfile.wayland-utils` with `podman`,
  saves it as an OCI archive, loads it into `.fiberpod`, then runs `wayland-info` with
  `--wayland-server`
- the same image also builds `/usr/local/bin/test_wayland_shm_window`, a minimal native
  Wayland client that creates an `xdg_surface`, allocates a `wl_shm` buffer, attaches it,
  and commits it against Podish's compositor
- the same image also includes `weston-clients`, so we can smoke-test real upstream clients
  such as `weston-simple-shm`; it also carries the `weston` runtime data files under
  `/usr/share/weston` that some clients like `weston-stacking` expect at runtime
- for manual rebuild/export/load/run of that image, use
  `scripts/debug-wayland-weston.sh`, which wraps `podman build`, `podman save --format oci-archive`,
  and `dotnet run --project Podish.Cli -- load -i ...`
