#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
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
    def default_history_mode(self) -> str:
        return str(self.data.get("default_history_mode", "orphan"))

    @property
    def default_author_name(self) -> str | None:
        value = self.data.get("default_author_name")
        return None if value in (None, "") else str(value)

    @property
    def default_author_email(self) -> str | None:
        value = self.data.get("default_author_email")
        return None if value in (None, "") else str(value)

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
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    command = ["git", *args]
    return subprocess.run(
        command,
        cwd=str(cwd or repo_root),
        check=True,
        text=True,
        capture_output=capture_output,
        env=env,
    )


def run_command(
    args: list[str],
    *,
    cwd: Path | None = None,
    capture_output: bool = False,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        args,
        cwd=str(cwd) if cwd is not None else None,
        check=True,
        text=True,
        capture_output=capture_output,
        env=env,
    )


def resolve_commit(repo_root: Path, ref: str) -> str:
    result = run_git(repo_root, ["rev-parse", "--verify", ref], capture_output=True)
    return result.stdout.strip()


def default_commit_message(source_commit: str) -> str:
    return f"public release, source {source_commit[:7]}"


def build_git_env(
    *,
    author_name: str | None,
    author_email: str | None,
) -> dict[str, str] | None:
    if not author_name and not author_email:
        return None

    env = os.environ.copy()
    if author_name:
        env["GIT_AUTHOR_NAME"] = author_name
        env["GIT_COMMITTER_NAME"] = author_name
    if author_email:
        env["GIT_AUTHOR_EMAIL"] = author_email
        env["GIT_COMMITTER_EMAIL"] = author_email
    return env


def resolve_latest_release_tag(repo: str) -> str:
    result = run_command(
        ["gh", "release", "view", "--repo", repo, "--json", "tagName", "-q", ".tagName"],
        capture_output=True,
    )
    tag = result.stdout.strip()
    if not tag:
        raise RuntimeError(f"Could not resolve latest release tag for {repo}.")
    return tag


def release_exists(repo: str, tag: str) -> bool:
    result = subprocess.run(
        ["gh", "release", "view", tag, "--repo", repo],
        text=True,
        capture_output=True,
    )
    return result.returncode == 0


def ensure_release_exists(repo: str, tag: str, source_ref: str, source_commit: str) -> None:
    if release_exists(repo, tag):
        return

    title = f"Release {tag}"
    notes = f"Automated wasm static library release for {source_ref} ({source_commit[:7]})."
    run_command(
        [
            "gh",
            "release",
            "create",
            tag,
            "--repo",
            repo,
            "--title",
            title,
            "--notes",
            notes,
        ]
    )


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
    history_mode: str,
    author_name: str | None,
    author_email: str | None,
    commit_message: str | None,
    push: bool,
    verify: bool,
) -> int:
    source_commit = resolve_commit(config.repo_root, source_ref)
    final_message = commit_message or default_commit_message(source_commit)

    print(f"name: {config.name}")
    print(f"source ref: {source_ref}")
    print(f"source commit: {source_commit}")
    print(f"history mode: {history_mode}")
    print(f"local export branch: {branch}")
    print(f"push target: {remote}/{remote_branch}")
    if history_mode == "append":
        try:
            run_git(config.repo_root, ["fetch", remote, remote_branch])
            remote_commit = resolve_commit(
                config.repo_root,
                f"refs/remotes/{remote}/{remote_branch}",
            )
            print(f"append base: {remote}/{remote_branch} ({remote_commit})")
        except subprocess.CalledProcessError:
            print(f"append base: {remote}/{remote_branch} (missing; export would fail)")
    print(f"commit author: {author_name or '(git default)'} <{author_email or '(git default)'}>")
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
    author_name: str | None,
    author_email: str | None,
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
    git_env = build_git_env(author_name=author_name, author_email=author_email)

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
        run_git(config.repo_root, ["commit", "-m", final_message], cwd=worktree_path, env=git_env)
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


def upload_fibercpu_wasm(
    config: ExportConfig,
    *,
    source_ref: str,
    repo: str,
    tag: str | None,
    asset_name: str,
    clobber: bool,
) -> int:
    source_commit = resolve_commit(config.repo_root, source_ref)
    project_path = config.repo_root / "Fiberish.X86" / "Fiberish.X86.csproj"
    static_lib_path = config.repo_root / "Fiberish.X86" / "build_native" / "browser-wasm" / "libfibercpu.a"

    resolved_tag = tag or resolve_latest_release_tag(repo)
    ensure_release_exists(repo, resolved_tag, source_ref, source_commit)

    print(f"building browser-wasm static library from {source_ref} ({source_commit})")
    run_command(
        [
            "dotnet",
            "build",
            str(project_path),
            "-c",
            "Release",
            "-p:RuntimeIdentifier=browser-wasm",
        ],
        cwd=config.repo_root,
    )

    if not static_lib_path.is_file():
        raise RuntimeError(f"Expected static library not found: {static_lib_path}")

    with tempfile.TemporaryDirectory(prefix="fibercpu-wasm-release-") as temp_dir:
        upload_path = Path(temp_dir) / asset_name
        shutil.copy2(static_lib_path, upload_path)

        upload_command = [
            "gh",
            "release",
            "upload",
            resolved_tag,
            str(upload_path),
            "--repo",
            repo,
        ]
        if clobber:
            upload_command.append("--clobber")

        print(f"uploading {asset_name} to {repo} release {resolved_tag}")
        run_command(upload_command, cwd=config.repo_root)

    print(f"uploaded {asset_name} to {repo} release {resolved_tag}")
    return 0


def export_append_history(
    config: ExportConfig,
    *,
    source_ref: str,
    branch: str,
    remote: str,
    remote_branch: str,
    author_name: str | None,
    author_email: str | None,
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
    git_env = build_git_env(author_name=author_name, author_email=author_email)

    run_git(config.repo_root, ["fetch", remote, remote_branch])
    remote_ref = f"refs/remotes/{remote}/{remote_branch}"
    remote_commit = resolve_commit(config.repo_root, remote_ref)

    run_git(config.repo_root, ["worktree", "add", "--detach", str(worktree_path), remote_commit])
    try:
        run_git(config.repo_root, ["switch", "-c", temp_branch], cwd=worktree_path)
        run_git(config.repo_root, ["rm", "-r", "-f", "--ignore-unmatch", "."], cwd=worktree_path)
        run_git(config.repo_root, ["clean", "-fdx"], cwd=worktree_path)
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
        run_git(config.repo_root, ["commit", "-m", final_message], cwd=worktree_path, env=git_env)
        run_git(config.repo_root, ["branch", "-f", branch, "HEAD"], cwd=worktree_path)
        run_git(config.repo_root, ["switch", "--detach", "HEAD"], cwd=worktree_path)
        run_git(config.repo_root, ["branch", "-D", temp_branch], cwd=worktree_path)

        if push:
            run_git(config.repo_root, ["push", remote, f"HEAD:{remote_branch}"], cwd=worktree_path)

        print(f"exported commit from {source_commit} on top of {remote}/{remote_branch} ({remote_commit})")
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
        subparser.add_argument(
            "--history-mode",
            choices=["append", "orphan"],
            default=config.default_history_mode,
        )
        subparser.add_argument("--author-name", default=config.default_author_name)
        subparser.add_argument("--author-email", default=config.default_author_email)
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

    fibercpu_parser = subparsers.add_parser(
        "upload-fibercpu-wasm",
        help="Build libfibercpu.a for browser-wasm and upload it to a GitHub release asset.",
    )
    fibercpu_parser.add_argument("--source-ref", default=config.default_source_ref)
    fibercpu_parser.add_argument("--repo", default="GiantNeko/fibercpu")
    fibercpu_parser.add_argument("--tag", help="Release tag to upload to. Defaults to the latest release tag.")
    fibercpu_parser.add_argument("--asset-name", default="libfibercpu-wasm.a")
    fibercpu_parser.add_argument(
        "--no-clobber",
        action="store_true",
        help="Do not overwrite an existing asset with the same name.",
    )

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
            history_mode=args.history_mode,
            author_name=args.author_name,
            author_email=args.author_email,
            commit_message=args.commit_message,
            push=bool(args.push),
            verify=bool(args.verify),
        )

    if args.command == "export":
        if args.history_mode == "append":
            return export_append_history(
                config,
                source_ref=args.source_ref,
                branch=args.branch,
                remote=args.remote,
                remote_branch=args.remote_branch,
                author_name=args.author_name,
                author_email=args.author_email,
                commit_message=args.commit_message,
                push=bool(args.push),
                verify=bool(args.verify),
                keep_worktree=bool(args.keep_worktree),
            )
        return export_public_tree(
            config,
            source_ref=args.source_ref,
            branch=args.branch,
            remote=args.remote,
            remote_branch=args.remote_branch,
            author_name=args.author_name,
            author_email=args.author_email,
            commit_message=args.commit_message,
            push=bool(args.push),
            verify=bool(args.verify),
            keep_worktree=bool(args.keep_worktree),
        )

    if args.command == "upload-fibercpu-wasm":
        return upload_fibercpu_wasm(
            config,
            source_ref=args.source_ref,
            repo=args.repo,
            tag=args.tag,
            asset_name=args.asset_name,
            clobber=not bool(args.no_clobber),
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
