# Public Export

This directory defines the repeatable workflow for publishing the browser-focused public snapshot from `main` onto the public remote branch.

## Layout

- `manifest.json`: export defaults, whitelist, template overrides, text patches, optional verification commands, and the default commit author.
- `templates/`: public-only file contents that replace the internal versions during export.
- `export.py`: Python driver that creates a detached worktree, rebuilds the public snapshot, applies templates and patches, optionally verifies, and can push to the configured remote branch.

## Typical Usage

Preview the plan without changing anything:

```bash
python3 public-export/export.py plan
```

Create or refresh the local export branch from the configured remote head:

```bash
python3 public-export/export.py export
```

Run the export and push it to `public/main`:

```bash
python3 public-export/export.py export --push
```

Keep the temporary worktree around for inspection:

```bash
python3 public-export/export.py export --keep-worktree
```

Verify the exported tree with the commands declared in the manifest:

```bash
python3 public-export/export.py export --verify
```

- Build `libfibercpu.a`, rename it to `libfibercpu-wasm.a`, and upload it to the latest `GiantNeko/fibercpu` release:

```bash
python3 public-export/export.py upload-fibercpu-wasm
```

- Upload the same asset to a specific `GiantNeko/fibercpu` release tag:

```bash
python3 public-export/export.py upload-fibercpu-wasm --tag v0.0.1
```

If the target release does not exist yet, the script creates it first and then uploads the asset.

- Force the legacy orphan-history flow instead of appending on top of the remote branch:

```bash
python3 public-export/export.py export --history-mode orphan
```

## Notes

- The tool exports from a committed ref, not from uncommitted changes in your current worktree.
- The default source ref is `main`, and the default push target is `public/main`.
- The default history mode is `append`, so pushes add a new commit on top of the current public branch head instead of force-replacing history.
- The default commit author is `GiantNeko <giantneko@icloud.com>`.
