#!/usr/bin/env python3
from __future__ import annotations

import argparse
import datetime
import json
import os
import plistlib
import shutil
import subprocess
import sys
from pathlib import Path


DEFAULT_REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PROJECT_DIR = DEFAULT_REPO_ROOT / "PodishApp"
DEFAULT_PROJECT_PATH = DEFAULT_PROJECT_DIR / "PodishApp.xcodeproj"
DEFAULT_TEMP_DIR = DEFAULT_REPO_ROOT / ".tmp" / "publish_altstore"
DEFAULT_SCHEME = "Podish"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build and publish an IPA to an AltStore repo.")
    parser.add_argument("--method", default="ad-hoc", choices=["ad-hoc", "development", "app-store"], help="Export method")
    parser.add_argument("--repo-url", default=os.environ.get("ALTSTORE_REPO_URL"), help="GitHub repository URL for the AltStore feed.")
    parser.add_argument(
        "--repo-name",
        default=os.environ.get("ALTSTORE_REPO_NAME"),
        help="Repository name for generated asset URLs. Defaults to the name parsed from --repo-url.",
    )
    parser.add_argument(
        "--project-dir",
        default=os.environ.get("PODISH_PROJECT_DIR", str(DEFAULT_PROJECT_DIR)),
        help="Path to the PodishApp project directory.",
    )
    parser.add_argument(
        "--project-path",
        default=os.environ.get("PODISH_PROJECT_PATH"),
        help="Path to the Xcode project. Defaults to <project-dir>/PodishApp.xcodeproj.",
    )
    parser.add_argument("--scheme", default=os.environ.get("PODISH_SCHEME", DEFAULT_SCHEME), help="Xcode scheme to archive.")
    parser.add_argument(
        "--temp-dir",
        default=os.environ.get("ALTSTORE_TEMP_DIR", str(DEFAULT_TEMP_DIR)),
        help="Scratch directory used during packaging and publishing.",
    )
    parser.add_argument(
        "--publish-author-name",
        default=os.environ.get("ALTSTORE_PUBLISH_AUTHOR_NAME"),
        help="Git author name used for AltStore repo commits.",
    )
    parser.add_argument(
        "--publish-author-email",
        default=os.environ.get("ALTSTORE_PUBLISH_AUTHOR_EMAIL"),
        help="Git author email used for AltStore repo commits.",
    )
    parser.add_argument(
        "--feed-name",
        default=os.environ.get("ALTSTORE_FEED_NAME", "Podish AltStore Repo"),
        help="Name stored in apps.json when bootstrapping a new feed.",
    )
    parser.add_argument(
        "--feed-identifier",
        default=os.environ.get("ALTSTORE_FEED_IDENTIFIER"),
        help="Identifier stored in apps.json when bootstrapping a new feed.",
    )
    parser.add_argument(
        "--developer-name",
        default=os.environ.get("ALTSTORE_DEVELOPER_NAME"),
        help="Developer name shown in the AltStore app entry.",
    )
    parser.add_argument(
        "--icon-url",
        default=os.environ.get("ALTSTORE_ICON_URL"),
        help="Public icon URL stored in the AltStore app entry.",
    )
    parser.add_argument(
        "--download-url-template",
        default=os.environ.get("ALTSTORE_DOWNLOAD_URL_TEMPLATE"),
        help="Optional format string for download URLs. Supported keys: repo_url, repo_name, owner, tag, ipa_filename.",
    )
    parser.add_argument(
        "--release-notes-template",
        default=os.environ.get("ALTSTORE_RELEASE_NOTES_TEMPLATE", "Automated release {version}"),
        help="Optional format string for GitHub release notes.",
    )
    parser.add_argument(
        "--description",
        default=os.environ.get("ALTSTORE_APP_DESCRIPTION", "x86 emulator for iOS"),
        help="Localized description stored in apps.json.",
    )
    return parser.parse_args()


def fatal(message: str) -> "NoReturn":
    print(f"Error: {message}", file=sys.stderr)
    raise SystemExit(1)


def run(cmd: list[str], cwd: Path | None = None, check: bool = True, env: dict[str, str] | None = None) -> str:
    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, env=env)
    if check and result.returncode != 0:
        print(f"STDOUT: {result.stdout}")
        print(f"STDERR: {result.stderr}")
        raise SystemExit(result.returncode)
    return result.stdout.strip()


def require_tool(tool: str, install_hint: str) -> None:
    if shutil.which(tool) is None:
        fatal(install_hint)


def parse_repo_owner_and_name(repo_url: str) -> tuple[str, str]:
    trimmed = repo_url.rstrip("/")
    if trimmed.endswith(".git"):
        trimmed = trimmed[:-4]

    marker = "github.com/"
    index = trimmed.find(marker)
    if index < 0:
        fatal(f"--repo-url must be a GitHub repository URL: {repo_url}")

    suffix = trimmed[index + len(marker):]
    parts = [part for part in suffix.split("/") if part]
    if len(parts) < 2:
        fatal(f"--repo-url must include both owner and repository name: {repo_url}")
    return parts[0], parts[1]


def build_publish_env(author_name: str, author_email: str) -> dict[str, str]:
    env = os.environ.copy()
    env["GIT_AUTHOR_NAME"] = author_name
    env["GIT_AUTHOR_EMAIL"] = author_email
    env["GIT_COMMITTER_NAME"] = author_name
    env["GIT_COMMITTER_EMAIL"] = author_email
    return env


def get_build_settings(project_path: Path, scheme: str) -> dict[str, str]:
    settings = run(
        [
            "xcodebuild",
            "-showBuildSettings",
            "-project",
            str(project_path),
            "-scheme",
            scheme,
            "-configuration",
            "Release",
        ]
    )
    settings_dict: dict[str, str] = {}
    for line in settings.splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        settings_dict[key.strip()] = value.strip()
    return settings_dict


def get_git_tag() -> tuple[str, str]:
    tag = run(["git", "describe", "--tags", "--exact-match"], check=False)
    if not tag or "fatal" in tag:
        fatal("Current commit has no git tag. Please tag your commit first (for example: git tag v0.0.5).")
    version = tag[1:] if tag.startswith("v") else tag
    return tag, version


def format_download_url(template: str | None, *, repo_url: str, repo_name: str, owner: str, tag: str, ipa_filename: str) -> str:
    if template:
        return template.format(
            repo_url=repo_url.rstrip("/"),
            repo_name=repo_name,
            owner=owner,
            tag=tag,
            ipa_filename=ipa_filename,
        )
    return f"{repo_url.rstrip('/')}/releases/download/{tag}/{ipa_filename}"


def resolve_config(args: argparse.Namespace) -> dict[str, object]:
    repo_url = args.repo_url
    if not repo_url:
        fatal("--repo-url or ALTSTORE_REPO_URL is required")

    owner, inferred_repo_name = parse_repo_owner_and_name(repo_url)
    repo_name = args.repo_name or inferred_repo_name

    project_dir = Path(args.project_dir).resolve()
    project_path = Path(args.project_path).resolve() if args.project_path else (project_dir / "PodishApp.xcodeproj")
    temp_dir = Path(args.temp_dir).resolve()

    if not project_dir.is_dir():
        fatal(f"project directory not found: {project_dir}")
    if not project_path.exists():
        fatal(f"project path not found: {project_path}")

    author_name = args.publish_author_name
    author_email = args.publish_author_email
    if not author_name or not author_email:
        fatal("--publish-author-name and --publish-author-email are required (or set ALTSTORE_PUBLISH_AUTHOR_NAME / ALTSTORE_PUBLISH_AUTHOR_EMAIL)")

    feed_identifier = args.feed_identifier or f"com.{owner.lower()}.{repo_name.replace('_', '-').replace('.', '-')}"
    developer_name = args.developer_name or author_name
    icon_url = args.icon_url or f"https://raw.githubusercontent.com/{owner}/{repo_name}/main/icon.png"

    return {
        "repo_url": repo_url,
        "repo_name": repo_name,
        "repo_owner": owner,
        "project_dir": project_dir,
        "project_path": project_path,
        "scheme": args.scheme,
        "temp_dir": temp_dir,
        "author_name": author_name,
        "author_email": author_email,
        "feed_name": args.feed_name,
        "feed_identifier": feed_identifier,
        "developer_name": developer_name,
        "icon_url": icon_url,
        "download_url_template": args.download_url_template,
        "release_notes_template": args.release_notes_template,
        "description": args.description,
    }


def main() -> None:
    args = parse_args()
    config = resolve_config(args)

    require_tool("gh", "'gh' CLI is not installed. Please install it and log in first.")
    require_tool("xcodebuild", "'xcodebuild' was not found. Are you on macOS with Xcode installed?")

    publish_env = build_publish_env(config["author_name"], config["author_email"])
    git_tag, version_num = get_git_tag()
    print(f"Using version from Git tag: {git_tag} (Version: {version_num})")

    temp_dir: Path = config["temp_dir"]
    if temp_dir.exists():
        shutil.rmtree(temp_dir)
    temp_dir.mkdir(parents=True, exist_ok=True)

    print("Fetching build settings...")
    settings = get_build_settings(config["project_path"], config["scheme"])
    bundle_id = settings.get("PRODUCT_BUNDLE_IDENTIFIER", "com.example.podish")
    app_name = settings.get("PRODUCT_NAME", "Podish")
    print(f"Release version: {version_num}")

    archive_path = temp_dir / f"{config['scheme']}.xcarchive"
    print(f"Archiving {config['scheme']} with version {version_num}...")
    run(
        [
            "xcodebuild",
            "archive",
            "-project",
            str(config["project_path"]),
            "-scheme",
            str(config["scheme"]),
            "-archivePath",
            str(archive_path),
            "-configuration",
            "Release",
            "-destination",
            "generic/platform=iOS",
            "SKIP_INSTALL=NO",
            f"MARKETING_VERSION={version_num}",
            "CURRENT_PROJECT_VERSION=1",
        ]
    )

    print("Creating IPA manually via Payload directory...")
    ipa_export_dir = temp_dir / "Export"
    ipa_export_dir.mkdir(exist_ok=True)

    app_bundle_parent = archive_path / "Products" / "Applications"
    app_bundles = [path for path in app_bundle_parent.iterdir() if path.suffix == ".app"]
    if not app_bundles:
        fatal("No .app bundle found in archive")

    app_bundle_path = app_bundles[0]
    payload_dir = temp_dir / "Payload"
    if payload_dir.exists():
        shutil.rmtree(payload_dir)
    payload_dir.mkdir()
    shutil.copytree(app_bundle_path, payload_dir / app_bundle_path.name)

    target_app_path = payload_dir / app_bundle_path.name
    target_info_plist = target_app_path / "Info.plist"
    print(f"Updating Info.plist in {app_bundle_path.name} to version {version_num}...")
    run(["plutil", "-replace", "CFBundleShortVersionString", "-string", version_num, str(target_info_plist)])
    run(["plutil", "-replace", "CFBundleVersion", "-string", "1", str(target_info_plist)])

    print(f"Re-signing {app_bundle_path.name} with Ad-hoc signature and stripping get-task-allow...")
    entitlements_plist = temp_dir / "entitlements.plist"
    run(["codesign", "-d", "--entitlements", str(entitlements_plist), str(target_app_path)])
    run(["plutil", "-convert", "xml1", str(entitlements_plist)], check=False)

    with entitlements_plist.open("rb") as fp:
        try:
            ent_data = plistlib.load(fp)
        except Exception:
            ent_data = {}

    if "get-task-allow" in ent_data:
        print("Removing get-task-allow entitlement...")
        del ent_data["get-task-allow"]

    with entitlements_plist.open("wb") as fp:
        plistlib.dump(ent_data, fp)

    run(["codesign", "--force", "--sign", "-", "--entitlements", str(entitlements_plist), "--deep", str(target_app_path)])

    ipa_path = ipa_export_dir / f"{app_name}.ipa"
    run(["zip", "-r", str(ipa_path), "Payload"], cwd=temp_dir)
    print(f"Manual IPA created at: {ipa_path}")

    if not ipa_path.exists():
        fatal("IPA not found in export directory")

    altstore_repo_dir = temp_dir / str(config["repo_name"])
    print(f"Cloning {config['repo_url']}...")
    if run(["git", "clone", str(config["repo_url"]), str(altstore_repo_dir)], check=False) != "" or not altstore_repo_dir.exists():
        altstore_repo_dir.mkdir(parents=True, exist_ok=True)
        run(["git", "init"], cwd=altstore_repo_dir)
        run(["git", "remote", "add", "origin", str(config["repo_url"])], cwd=altstore_repo_dir, check=False)
    else:
        run(["git", "fetch", "origin", "main"], cwd=altstore_repo_dir, check=False)

    publish_branch = f"altstore-publish-{version_num}"
    run(["git", "checkout", "--orphan", publish_branch], cwd=altstore_repo_dir, check=False)
    run(["git", "rm", "-r", "-f", "--ignore-unmatch", "."], cwd=altstore_repo_dir, check=False)
    run(["git", "clean", "-fdx"], cwd=altstore_repo_dir, check=False)
    run(["git", "config", "user.name", str(config["author_name"])], cwd=altstore_repo_dir)
    run(["git", "config", "user.email", str(config["author_email"])], cwd=altstore_repo_dir)

    main_icon = Path(config["project_dir"]) / "icon.png"
    if main_icon.exists():
        shutil.copy(main_icon, altstore_repo_dir / "icon.png")

    apps_json_path = altstore_repo_dir / "apps.json"
    if apps_json_path.exists():
        data = json.loads(apps_json_path.read_text())
    else:
        data = {"name": config["feed_name"], "identifier": config["feed_identifier"], "apps": []}

    app_entry = next((app for app in data.get("apps", []) if app.get("bundleIdentifier") == bundle_id), None)
    if not app_entry:
        app_entry = {
            "name": app_name,
            "bundleIdentifier": bundle_id,
            "developerName": config["developer_name"],
            "localizedDescription": config["description"],
            "iconURL": config["icon_url"],
            "versions": [],
        }
        data.setdefault("apps", []).append(app_entry)

    download_url = format_download_url(
        str(config["download_url_template"]) if config["download_url_template"] else None,
        repo_url=str(config["repo_url"]),
        repo_name=str(config["repo_name"]),
        owner=str(config["repo_owner"]),
        tag=git_tag,
        ipa_filename=ipa_path.name,
    )
    new_version_entry = {
        "version": version_num,
        "date": datetime.datetime.now().strftime("%Y-%m-%d"),
        "localizedDescription": f"Release {version_num}",
        "downloadURL": download_url,
        "size": ipa_path.stat().st_size,
    }

    app_entry.setdefault("versions", [])
    app_entry["versions"] = [version for version in app_entry["versions"] if version["version"] != version_num]
    app_entry["versions"].insert(0, new_version_entry)
    app_entry["version"] = version_num
    app_entry["versionDate"] = new_version_entry["date"]
    app_entry["versionDescription"] = new_version_entry["localizedDescription"]
    app_entry["downloadURL"] = new_version_entry["downloadURL"]
    app_entry["size"] = new_version_entry["size"]

    apps_json_path.write_text(json.dumps(data, indent=2) + "\n")
    release_json_path = temp_dir / "apps.json"
    shutil.copy(apps_json_path, release_json_path)

    print("Committing and pushing to AltStore repo...")
    run(["git", "add", "apps.json", "icon.png"], cwd=altstore_repo_dir, check=False)
    run(["git", "add", "apps.json", "icon.png"], cwd=altstore_repo_dir)
    run(["git", "commit", "-m", f"Update to version {version_num}"], cwd=altstore_repo_dir, check=False, env=publish_env)
    run(["git", "push", "--force", "origin", "HEAD:main"], cwd=altstore_repo_dir)

    release_notes = str(config["release_notes_template"]).format(
        version=version_num,
        tag=git_tag,
        repo_url=config["repo_url"],
        repo_name=config["repo_name"],
    )
    print(f"Creating/Uploading to GitHub Release {git_tag}...")
    run(
        [
            "gh",
            "release",
            "create",
            git_tag,
            "--repo",
            str(config["repo_url"]),
            "--title",
            f"Release {version_num}",
            "--notes",
            release_notes,
        ],
        check=False,
    )
    run(["gh", "release", "upload", git_tag, str(ipa_path), str(release_json_path), "--repo", str(config["repo_url"]), "--clobber"])

    print("\nSUCCESS!")
    print(f"IPA published: {download_url}")
    print(f"AltStore JSON updated in {config['repo_url']}")


if __name__ == "__main__":
    main()
