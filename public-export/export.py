#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class ExportConfig:
    repo_root: Path
    tool_root: Path
    manifest_path: Path
    data: dict[str, Any]

    @property
    def name(self) -> str:
        return str(self.data["name"])

    @property
    def default_source_ref(self) -> str:
        return str(self.data["default_source_ref"])

    @property
    def default_branch(self) -> str:
        return str(self.data["default_branch"])

    @property
    def default_remote(self) -> str:
        return str(self.data["default_remote"])

    @property
    def default_remote_branch(self) -> str:
        return str(self.data["default_remote_branch"])

    @property
    def worktree_root(self) -> Path:
        return self.repo_root / str(self.data["worktree_root"])

    @property
    def restore_paths(self) -> list[str]:
        return [str(item) for item in self.data["restore_paths"]]

    @property
    def template_overrides(self) -> dict[str, str]:
        return {
            str(repo_path): str(template_path)
            for repo_path, template_path in self.data["template_overrides"].items()
        }

    @property
    def text_replacements(self) -> list[dict[str, str]]:
        return [dict(item) for item in self.data.get("text_replacements", [])]

    @property
    def verify_commands(self) -> list[list[str]]:
        return [
            [str(token) for token in command]
            for command in self.data.get("verify_commands", [])
        ]


def load_config() -> ExportConfig:
    tool_root = Path(__file__).resolve().parent
    repo_root = tool_root.parent
    manifest_path = tool_root / "manifest.json"
    data = json.loads(manifest_path.read_text(encoding="utf-8"))
    return ExportConfig(
        repo_root=repo_root,
        tool_root=tool_root,
        manifest_path=manifest_path,
        data=data,
    )


def run_git(
    repo_root: Path,
    args: list[str],
    *,
    cwd: Path | None = None,
    capture_output: bool = False,
) -> subprocess.CompletedProcess[str]:
    command = ["git", *args]
    return subprocess.run(
        command,
        cwd=str(cwd or repo_root),
        check=True,
        text=True,
        capture_output=capture_output,
    )


def resolve_commit(repo_root: Path, ref: str) -> str:
    result = run_git(repo_root, ["rev-parse", "--verify", ref], capture_output=True)
    return result.stdout.strip()


def default_commit_message(source_commit: str) -> str:
    return f"public release, source {source_commit[:7]}"


def copy_template(config: ExportConfig, template_rel_path: str, destination: Path) -> None:
    template_path = config.tool_root / template_rel_path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(template_path.read_text(encoding="utf-8"), encoding="utf-8")


def apply_text_replacements(config: ExportConfig, worktree_path: Path) -> None:
    for replacement in config.text_replacements:
        target_path = worktree_path / replacement["path"]
        original = target_path.read_text(encoding="utf-8")
        search = replacement["search"]
        replace = replacement["replace"]
        if search not in original:
            raise RuntimeError(
                f"Text replacement failed for {replacement['path']}: "
                f"search block not found.\nDescription: {replacement.get('description', '(none)')}"
            )
        updated = original.replace(search, replace, 1)
        target_path.write_text(updated, encoding="utf-8")


def print_plan(
    config: ExportConfig,
    *,
    source_ref: str,
    branch: str,
    remote: str,
    remote_branch: str,
    commit_message: str | None,
    push: bool,
    verify: bool,
) -> int:
    source_commit = resolve_commit(config.repo_root, source_ref)
    final_message = commit_message or default_commit_message(source_commit)

    print(f"name: {config.name}")
    print(f"source ref: {source_ref}")
    print(f"source commit: {source_commit}")
    print(f"orphan branch: {branch}")
    print(f"push target: {remote}/{remote_branch}")
    print(f"commit message: {final_message}")
    print("restore paths:")
    for path in config.restore_paths:
        print(f"  - {path}")
    print("template overrides:")
    for repo_path, template_path in config.template_overrides.items():
        print(f"  - {repo_path} <- public-export/{template_path}")
    print("text replacements:")
    for replacement in config.text_replacements:
        print(f"  - {replacement['path']}: {replacement.get('description', 'replacement')}")
    if verify:
        print("verify commands:")
        for command in config.verify_commands:
            print(f"  - {' '.join(command)}")
    else:
        print("verify commands: skipped")
    print(f"push after export: {'yes' if push else 'no'}")
    return 0


def ensure_empty_worktree_path(path: Path) -> None:
    if path.exists():
        raise RuntimeError(f"Worktree path already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)


def build_worktree_path(config: ExportConfig, branch: str) -> Path:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    return config.worktree_root / f"{branch}-{stamp}"


def build_temp_branch_name(branch: str) -> str:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    return f"{branch}-export-tmp-{stamp}"


def remove_worktree(config: ExportConfig, worktree_path: Path) -> None:
    try:
        run_git(config.repo_root, ["worktree", "remove", "--force", str(worktree_path)])
    except subprocess.CalledProcessError:
        shutil.rmtree(worktree_path, ignore_errors=True)


def export_public_tree(
    config: ExportConfig,
    *,
    source_ref: str,
    branch: str,
    remote: str,
    remote_branch: str,
    commit_message: str | None,
    push: bool,
    verify: bool,
    keep_worktree: bool,
) -> int:
    source_commit = resolve_commit(config.repo_root, source_ref)
    final_message = commit_message or default_commit_message(source_commit)
    worktree_path = build_worktree_path(config, branch)
    temp_branch = build_temp_branch_name(branch)
    ensure_empty_worktree_path(worktree_path)

    run_git(config.repo_root, ["worktree", "add", "--detach", str(worktree_path), source_commit])
    try:
        run_git(config.repo_root, ["switch", "--orphan", temp_branch], cwd=worktree_path)
        run_git(config.repo_root, ["rm", "-r", "-f", "--ignore-unmatch", "."], cwd=worktree_path)
        run_git(
            config.repo_root,
            ["restore", "--source", source_commit, "--staged", "--worktree", "--", *config.restore_paths],
            cwd=worktree_path,
        )

        for repo_path, template_path in config.template_overrides.items():
            copy_template(config, template_path, worktree_path / repo_path)

        apply_text_replacements(config, worktree_path)

        if verify:
            for command in config.verify_commands:
                subprocess.run(command, cwd=str(worktree_path), check=True, text=True)

        run_git(config.repo_root, ["add", "--all"], cwd=worktree_path)
        run_git(config.repo_root, ["commit", "-m", final_message], cwd=worktree_path)
        run_git(config.repo_root, ["branch", "-f", branch, "HEAD"], cwd=worktree_path)
        run_git(config.repo_root, ["switch", "--detach", "HEAD"], cwd=worktree_path)
        run_git(config.repo_root, ["branch", "-D", temp_branch], cwd=worktree_path)

        if push:
            run_git(
                config.repo_root,
                ["push", "--force", remote, f"HEAD:{remote_branch}"],
                cwd=worktree_path,
            )

        print(f"exported commit from {source_commit} into orphan branch {branch}")
        if push:
            print(f"pushed to {remote}/{remote_branch}")
        return 0
    finally:
        if keep_worktree:
            print(f"kept worktree at {worktree_path}")
        else:
            remove_worktree(config, worktree_path)
            print(f"removed temporary worktree {worktree_path}")


def parse_args(argv: list[str]) -> argparse.Namespace:
    config = load_config()
    parser = argparse.ArgumentParser(
        description="Create a public orphan export from a source ref using manifest-driven rules."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    def add_shared_flags(subparser: argparse.ArgumentParser) -> None:
        subparser.add_argument("--source-ref", default=config.default_source_ref)
        subparser.add_argument("--branch", default=config.default_branch)
        subparser.add_argument("--remote", default=config.default_remote)
        subparser.add_argument("--remote-branch", default=config.default_remote_branch)
        subparser.add_argument("--commit-message")

    plan_parser = subparsers.add_parser("plan", help="Show the export plan.")
    add_shared_flags(plan_parser)
    plan_parser.add_argument("--push", action="store_true", help="Show push target in the plan as enabled.")
    plan_parser.add_argument("--verify", action="store_true", help="Show verification commands in the plan.")

    export_parser = subparsers.add_parser("export", help="Run the export.")
    add_shared_flags(export_parser)
    export_parser.add_argument("--push", action="store_true", help="Force-push the exported commit.")
    export_parser.add_argument("--verify", action="store_true", help="Run manifest verification commands before committing.")
    export_parser.add_argument("--keep-worktree", action="store_true", help="Keep the detached worktree after the export completes.")

    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    config = load_config()

    if args.command == "plan":
        return print_plan(
            config,
            source_ref=args.source_ref,
            branch=args.branch,
            remote=args.remote,
            remote_branch=args.remote_branch,
            commit_message=args.commit_message,
            push=bool(args.push),
            verify=bool(args.verify),
        )

    if args.command == "export":
        return export_public_tree(
            config,
            source_ref=args.source_ref,
            branch=args.branch,
            remote=args.remote,
            remote_branch=args.remote_branch,
            commit_message=args.commit_message,
            push=bool(args.push),
            verify=bool(args.verify),
            keep_worktree=bool(args.keep_worktree),
        )

    raise AssertionError(f"Unhandled command: {args.command}")


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except subprocess.CalledProcessError as exc:
        print(f"command failed with exit code {exc.returncode}: {' '.join(exc.cmd)}", file=sys.stderr)
        if exc.stdout:
            print(exc.stdout, file=sys.stderr, end="")
        if exc.stderr:
            print(exc.stderr, file=sys.stderr, end="")
        raise
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
