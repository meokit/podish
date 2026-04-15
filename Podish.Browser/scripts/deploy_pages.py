#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import shutil
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path


DEFAULT_PUBLISH_DIR = Path("Podish.Browser/bin/Release/net10.0/publish/wwwroot")
DEFAULT_CLOUDFLARE_DIR = Path("Podish.Browser/cloudflare-pages")
DEFAULT_ROOTFS_DIR = Path("Podish.Browser/frontend/public/rootfs")
STATIC_IMMUTABLE_CACHE = "public, max-age=31536000, immutable"
IMAGE_CACHE = "public, max-age=60, s-maxage=300"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Deploy Podish.Browser publish output to Cloudflare Pages and upload rootfs assets to R2.",
    )
    parser.add_argument("--project-name", required=True, help="Cloudflare Pages project name.")
    parser.add_argument("--r2-bucket", required=True, help="R2 bucket name for rootfs assets.")
    parser.add_argument("--publish-dir", default=str(DEFAULT_PUBLISH_DIR), help="Path to the publish wwwroot directory.")
    parser.add_argument(
        "--rootfs-dir",
        default=str(DEFAULT_ROOTFS_DIR),
        help="Path to the build-rootfs output directory to upload to R2.",
    )
    parser.add_argument(
        "--cloudflare-dir",
        default=str(DEFAULT_CLOUDFLARE_DIR),
        help="Directory that contains the Pages Function source.",
    )
    parser.add_argument("--r2-prefix", default="rootfs", help="Object prefix inside the R2 bucket.")
    parser.add_argument("--branch", help="Optional Pages branch name for preview deployments.")
    parser.add_argument("--commit-hash", help="Optional commit hash to pass through to wrangler.")
    parser.add_argument(
        "--compatibility-date",
        help="Wrangler compatibility date. Defaults to the existing wrangler.jsonc value when present, otherwise the current UTC date.",
    )
    parser.add_argument("--wrangler-bin", default="wrangler", help="Wrangler executable to invoke.")
    parser.add_argument("--skip-upload", action="store_true", help="Skip uploading rootfs objects to R2.")
    parser.add_argument("--skip-deploy", action="store_true", help="Skip running wrangler pages deploy.")
    parser.add_argument(
        "--force-rootfs-upload",
        action="store_true",
        help="Upload all rootfs objects even if they already exist remotely.",
    )
    parser.add_argument("--keep-staging", action="store_true", help="Keep the generated .dist directory after deploy.")
    return parser.parse_args()


def main() -> None:
    args = parse_args()

    publish_dir = Path(args.publish_dir).resolve()
    rootfs_dir = Path(args.rootfs_dir).resolve()
    cloudflare_dir = Path(args.cloudflare_dir).resolve()
    r2_prefix = args.r2_prefix.strip("/")

    if not r2_prefix:
        raise SystemExit("--r2-prefix must not be empty")

    if args.skip_upload and args.skip_deploy:
        raise SystemExit("Nothing to do: both --skip-upload and --skip-deploy were provided")

    if not args.skip_upload:
        if not rootfs_dir.is_dir():
            raise SystemExit(f"rootfs directory not found: {rootfs_dir}")
        print(f"Uploading rootfs objects from {rootfs_dir} to R2 bucket {args.r2_bucket}")
        upload_rootfs(
            wrangler_bin=args.wrangler_bin,
            bucket_name=args.r2_bucket,
            rootfs_dir=rootfs_dir,
            r2_prefix=r2_prefix,
            cwd=rootfs_dir,
            skip_existing=not args.force_rootfs_upload,
        )

    if not args.skip_deploy:
        if not publish_dir.is_dir():
            raise SystemExit(f"Publish directory not found: {publish_dir}")
        if not cloudflare_dir.is_dir():
            raise SystemExit(f"Cloudflare directory not found: {cloudflare_dir}")
        staging_dir = cloudflare_dir / ".dist"
        compatibility_date = resolve_compatibility_date(
            cloudflare_dir=cloudflare_dir,
            requested_compatibility_date=args.compatibility_date,
        )

        print(f"Staging static Pages assets from {publish_dir}")
        stage_static_assets(publish_dir, staging_dir)

        print(f"Writing wrangler config in {cloudflare_dir}")
        write_wrangler_config(
            cloudflare_dir=cloudflare_dir,
            project_name=args.project_name,
            bucket_name=args.r2_bucket,
            compatibility_date=compatibility_date,
            rootfs_prefix=r2_prefix,
        )

        print(f"Deploying Pages project {args.project_name}")
        deploy_pages(
            wrangler_bin=args.wrangler_bin,
            project_name=args.project_name,
            staging_dir=staging_dir,
            cwd=cloudflare_dir,
            branch=args.branch,
            commit_hash=args.commit_hash,
        )

    if not args.skip_deploy and not args.keep_staging:
        shutil.rmtree(staging_dir, ignore_errors=True)
        print(f"Removed staging directory {staging_dir}")


def stage_static_assets(publish_dir: Path, staging_dir: Path) -> None:
    if staging_dir.exists():
        shutil.rmtree(staging_dir)
    staging_dir.mkdir(parents=True, exist_ok=True)

    for source in publish_dir.rglob("*"):
        relative_path = source.relative_to(publish_dir)
        if should_skip_publish_path(source, relative_path):
            continue
        destination = staging_dir / relative_path
        if source.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)

    write_headers_file(staging_dir / "_headers")


def should_skip_publish_path(source_path: Path, relative_path: Path) -> bool:
    parts = relative_path.parts
    if not parts:
        return False
    if parts[0] == "rootfs":
        return True
    if relative_path.suffix == ".wasm":
        compressed_path = source_path.with_name(f"{source_path.name}.br")
        if compressed_path.exists():
            return True
    if relative_path.suffix in {".br", ".gz"}:
        if relative_path.suffix == ".br" and relative_path.name.endswith(".wasm.br"):
            return False
        return True
    return any(part == ".DS_Store" for part in parts)


def write_headers_file(headers_path: Path) -> None:
    headers_path.write_text(
        """/*
  Cross-Origin-Opener-Policy: same-origin
  Cross-Origin-Embedder-Policy: require-corp
  Cross-Origin-Resource-Policy: same-origin

/_framework/*
  Cache-Control: public, max-age=31536000, immutable

/_framework/*.wasm.br
  Content-Type: application/wasm
  Content-Encoding: br
  Cache-Control: public, max-age=31536000, immutable

/assets/*
  Cache-Control: public, max-age=31536000, immutable

/*.mjs
  Cache-Control: public, max-age=31536000, immutable

/*.js
  Cache-Control: public, max-age=31536000, immutable

/*.css
  Cache-Control: public, max-age=31536000, immutable

/*.wasm
  Cache-Control: public, max-age=31536000, immutable
""",
        encoding="utf-8",
    )


def resolve_compatibility_date(*, cloudflare_dir: Path, requested_compatibility_date: str | None) -> str:
    if requested_compatibility_date:
        return requested_compatibility_date

    config_path = cloudflare_dir / "wrangler.jsonc"
    if config_path.is_file():
        try:
            existing_config = json.loads(config_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            existing_config = None
        if isinstance(existing_config, dict):
            existing_date = existing_config.get("compatibility_date")
            if isinstance(existing_date, str) and existing_date:
                return existing_date

    return datetime.now(timezone.utc).date().isoformat()


def write_wrangler_config(
    *,
    cloudflare_dir: Path,
    project_name: str,
    bucket_name: str,
    compatibility_date: str,
    rootfs_prefix: str,
) -> None:
    config = {
        "$schema": "../frontend/node_modules/wrangler/config-schema.json",
        "name": project_name,
        "compatibility_date": compatibility_date,
        "pages_build_output_dir": "./.dist",
        "vars": {
            "ROOTFS_PREFIX": rootfs_prefix,
        },
        "r2_buckets": [
            {
                "binding": "ROOTFS_BUCKET",
                "bucket_name": bucket_name,
            }
        ],
    }
    config_path = cloudflare_dir / "wrangler.jsonc"
    config_path.write_text(f"{json.dumps(config, indent=2)}\n", encoding="utf-8")


def upload_rootfs(
    *,
    wrangler_bin: str,
    bucket_name: str,
    rootfs_dir: Path,
    r2_prefix: str,
    cwd: Path,
    skip_existing: bool,
) -> None:
    files = sorted(
        path
        for path in rootfs_dir.rglob("*")
        if path.is_file() and path.name != ".DS_Store" and path.suffix not in {".br", ".gz"}
    )
    if not files:
        raise SystemExit(f"No rootfs files found under {rootfs_dir}")

    for path in files:
        relative_path = path.relative_to(rootfs_dir).as_posix()
        object_key = "/".join(part for part in (r2_prefix, relative_path) if part)
        content_type = guess_content_type(path)
        cache_control = IMAGE_CACHE if relative_path == "image.json" else STATIC_IMMUTABLE_CACHE
        should_skip_existing = skip_existing and relative_path != "image.json"

        if should_skip_existing and remote_object_exists(
            wrangler_bin=wrangler_bin,
            bucket_name=bucket_name,
            object_key=object_key,
            cwd=cwd,
        ):
            print(f"= Skipping existing rootfs object: {bucket_name}/{object_key}")
            continue

        command = [
            wrangler_bin,
            "r2",
            "object",
            "put",
            f"{bucket_name}/{object_key}",
            "--file",
            str(path),
            "--content-type",
            content_type,
            "--cache-control",
            cache_control,
            "--remote",
        ]
        run(command, cwd=cwd)


def deploy_pages(
    *,
    wrangler_bin: str,
    project_name: str,
    staging_dir: Path,
    cwd: Path,
    branch: str | None,
    commit_hash: str | None,
) -> None:
    command = [
        wrangler_bin,
        "pages",
        "deploy",
        str(staging_dir),
        "--project-name",
        project_name,
    ]
    if branch:
        command.extend(["--branch", branch])
    if commit_hash:
        command.extend(["--commit-hash", commit_hash])
    run(command, cwd=cwd)


def guess_content_type(path: Path) -> str:
    if path.suffix == ".wasm":
        return "application/wasm"
    if path.suffix == ".mjs":
        return "text/javascript; charset=utf-8"
    if path.suffix == ".json":
        return "application/json; charset=utf-8"
    guessed_type, _ = mimetypes.guess_type(path.name)
    return guessed_type or "application/octet-stream"


def remote_object_exists(*, wrangler_bin: str, bucket_name: str, object_key: str, cwd: Path) -> bool:
    with tempfile.NamedTemporaryFile(prefix="deploy-pages-r2-check-", delete=False) as temporary_file:
        temp_path = Path(temporary_file.name)
    try:
        result = subprocess.run(
            [
                wrangler_bin,
                "r2",
                "object",
                "get",
                f"{bucket_name}/{object_key}",
                "--file",
                str(temp_path),
                "--remote",
            ],
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            check=False,
        )
        return result.returncode == 0
    finally:
        try:
            os.unlink(temp_path)
        except FileNotFoundError:
            pass


def run(command: list[str], *, cwd: Path) -> None:
    print("+", " ".join(command))
    subprocess.run(command, cwd=cwd, check=True)


if __name__ == "__main__":
    main()
