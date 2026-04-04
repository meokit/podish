# Public Export

This directory defines the repeatable workflow for publishing the browser-focused public snapshot from `main` into an orphan branch and pushing it to the public remote.

## Layout

- `manifest.json`: export defaults, whitelist, template overrides, text patches, and optional verification commands.
- `templates/`: public-only file contents that replace the internal versions during export.
- `export.py`: Python driver that creates a detached worktree, rebuilds the orphan branch, applies templates and patches, optionally verifies, and can force-push to the configured remote branch.

## Typical Usage

Preview the plan without changing anything:

```bash
python3 public-export/export.py plan
```

Create or refresh the orphan branch locally:

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

## Notes

- The tool exports from a committed ref, not from uncommitted changes in your current worktree.
- The default source ref is `main`, and the default push target is `public/main`.
- The exported branch history is intentionally replaced with a single root commit.
