# pjdfstest Runner

- Upstream repository: <https://github.com/pjd/pjdfstest>
- Local checkout path: `tests/pjdfstest/upstream/`
- Pinned upstream commit: `tests/pjdfstest/UPSTREAM_COMMIT`
- Runner entrypoint: `Podish.PjdFs/Podish.PjdFs.csproj`
- Default filesystem backend: `overlay-silkfs`

The checkout under `tests/pjdfstest/upstream/` is intentionally ignored by git.
`Podish.PjdFs` clones the upstream repository on demand and checks out the pinned
commit when the local checkout is missing.

The intent is to keep this suite independent from the existing `pytest` test flow
while avoiding a large vendored subtree in the repository.

## Prerequisites

- `dotnet`
- `podman`

Use the built-in doctor command to validate the local environment:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- doctor
```

## Discover cases

List all discovered cases:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- list
```

List only matching cases:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- list --filter rename/
```

## Run cases

Run the whole suite with the default backend (`overlay-silkfs`):

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- run
```

Run only a subset:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- run --filter rename/19.t --jobs 1
```

Force asset rebuild:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- run --rebuild-assets
```

Keep per-case workdirs for debugging:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- run --keep-workdir
```

## Backend matrix

Run `tmpfs` directly:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- \
  run --fs-backend tmpfs \
  --output-json build/pjdfstest/tmpfs-latest-summary.json
```

Run `overlayfs` on `layerfs + tmpfs`:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- \
  run --fs-backend overlay-tmpfs \
  --output-json build/pjdfstest/overlay-tmpfs-latest-summary.json
```

Run `silkfs` directly:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- \
  run --fs-backend silkfs \
  --output-json build/pjdfstest/silkfs-latest-summary.json
```

Run `overlayfs` on `layerfs + silkfs`:

```bash
dotnet run --project Podish.PjdFs/Podish.PjdFs.csproj -- \
  run --fs-backend overlay-silkfs \
  --output-json build/pjdfstest/overlay-silkfs-latest-summary.json
```

## Output layout

Each run writes a timestamped directory under:

```text
build/pjdfstest/runs/<utc-timestamp>/
```

Important artifacts:

- `summary.json`: full machine-readable result for that run
- `report.md`: markdown summary for quick inspection
- `work/`: per-case working directories when `--keep-workdir` is enabled

If `--output-json` is provided, the same summary is also copied to the requested
path, which is useful for keeping a stable `*-latest-summary.json` pointer per
backend.
