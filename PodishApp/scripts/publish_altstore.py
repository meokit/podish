import os
import subprocess
import json
import datetime
import plistlib
import shutil
import sys
import argparse

# --- CONFIGURATION ---
REPO_URL = "https://github.com/GiantNeko/podish-altstore"
REPO_NAME = "podish-altstore"
PROJECT_DIR = "/Users/jiangyiheng/repos/x86emu/PodishApp"
PROJECT_PATH = f"{PROJECT_DIR}/PodishApp.xcodeproj"
SCHEME = "Podish"
TEMP_DIR = "/Users/jiangyiheng/repos/x86emu/.tmp/publish_altstore"
# ---------------------

def run(cmd, cwd=None, check=True):
    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    if check and result.returncode != 0:
        print(f"STDOUT: {result.stdout}")
        print(f"STDERR: {result.stderr}")
        sys.exit(result.returncode)
    return result.stdout.strip()

def get_build_settings():
    settings = run([
        "xcodebuild", "-showBuildSettings",
        "-project", PROJECT_PATH,
        "-scheme", SCHEME,
        "-configuration", "Release"
    ])
    settings_dict = {}
    for line in settings.splitlines():
        if "=" in line:
            parts = line.split("=", 1)
            settings_dict[parts[0].strip()] = parts[1].strip()
    return settings_dict

def check_requirements():
    if shutil.which("gh") is None:
        print("Error: 'gh' CLI is not installed. Please install it and login.")
        sys.exit(1)
    if shutil.which("xcodebuild") is None:
        print("Error: 'xcodebuild' is not found. Are you on macOS with Xcode installed?")
        sys.exit(1)

def main():
    check_requirements()
    parser = argparse.ArgumentParser(description="Build and publish IPA to AltStore")
    parser.add_argument("--method", default="ad-hoc", choices=["ad-hoc", "development", "app-store"], help="Export method")
    args = parser.parse_args()

    if os.path.exists(TEMP_DIR):
        shutil.rmtree(TEMP_DIR)
    os.makedirs(TEMP_DIR)

    # 0. Get Build Settings
    print("Fetching build settings...")
    settings = get_build_settings()
    bundle_id = settings.get("PRODUCT_BUNDLE_IDENTIFIER", "com.meokit.podish")
    app_name = settings.get("PRODUCT_NAME", "Podish")
    
    # 1. Archive
    archive_path = f"{TEMP_DIR}/{app_name}.xcarchive"
    print(f"Archiving {app_name}...")
    run([
        "xcodebuild", "archive",
        "-project", PROJECT_PATH,
        "-scheme", SCHEME,
        "-archivePath", archive_path,
        "-configuration", "Release",
        "-destination", "generic/platform=iOS",
        "SKIP_INSTALL=NO"
    ])

    # 2. Manual IPA Creation (Payload method)
    # This is more reliable for AltStore as it avoids xcodebuild signing checks.
    print("Creating IPA manually via Payload directory...")
    ipa_export_dir = f"{TEMP_DIR}/Export"
    os.makedirs(ipa_export_dir)
    
    # Find the app bundle inside the archive
    app_bundle_parent = f"{archive_path}/Products/Applications"
    app_bundles = [f for f in os.listdir(app_bundle_parent) if f.endswith(".app")]
    if not app_bundles:
        print("Error: No .app bundle found in archive")
        sys.exit(1)
    
    app_bundle_name = app_bundles[0]
    app_bundle_path = os.path.join(app_bundle_parent, app_bundle_name)
    
    # Create Payload structure
    payload_dir = f"{TEMP_DIR}/Payload"
    if os.path.exists(payload_dir):
        shutil.rmtree(payload_dir)
    os.makedirs(payload_dir)
    
    # Copy .app to Payload
    shutil.copytree(app_bundle_path, f"{payload_dir}/{app_bundle_name}")
    
    # Zip to .ipa
    ipa_filename = f"{app_name}.ipa"
    ipa_path = os.path.join(ipa_export_dir, ipa_filename)
    
    # Using zip command for better compatibility with IPA structure
    run(["zip", "-r", ipa_path, "Payload"], cwd=TEMP_DIR)
    
    print(f"Manual IPA created at: {ipa_path}")

    ipa_path = None
    for f in os.listdir(ipa_export_dir):
        if f.endswith(".ipa"):
            ipa_path = os.path.join(ipa_export_dir, f)
            break
    
    if not ipa_path:
        print("Error: IPA not found in export directory")
        sys.exit(1)
    
    ipa_filename = os.path.basename(ipa_path)

    # 3. Get Version Info from Archive's Info.plist
    # Find the app bundle inside the archive
    app_bundle_dir = f"{archive_path}/Products/Applications"
    app_bundle_name = [f for f in os.listdir(app_bundle_dir) if f.endswith(".app")][0]
    info_plist_path = f"{app_bundle_dir}/{app_bundle_name}/Info.plist"
    
    with open(info_plist_path, "rb") as f:
        info_plist = plistlib.load(f)
    
    marketing_version = info_plist.get("CFBundleShortVersionString", "0.0")
    build_version = info_plist.get("CFBundleVersion", "1")
    full_version = f"{marketing_version}.{build_version}"
    tag = f"v{full_version}"
    print(f"Release version: {full_version}")

    # 4. Clone/Prepare AltStore Repository
    altstore_repo_dir = f"{TEMP_DIR}/{REPO_NAME}"
    print(f"Cloning {REPO_URL}...")
    if run(["git", "clone", REPO_URL, altstore_repo_dir], check=False) != "" or not os.path.exists(altstore_repo_dir):
        if not os.path.exists(altstore_repo_dir):
            os.makedirs(altstore_repo_dir)
        run(["git", "init"], cwd=altstore_repo_dir)
        run(["git", "remote", "add", "origin", REPO_URL], cwd=altstore_repo_dir, check=False)
        # Ensure we are on 'main'
        run(["git", "checkout", "-b", "main"], cwd=altstore_repo_dir, check=False)

    # Copy icon if it exists in the main project
    main_icon = f"{PROJECT_DIR}/icon.png"
    if os.path.exists(main_icon):
        shutil.copy(main_icon, f"{altstore_repo_dir}/icon.png")

    apps_json_path = f"{altstore_repo_dir}/apps.json"
    if os.path.exists(apps_json_path):
        with open(apps_json_path, "r") as f:
            data = json.load(f)
    else:
        data = {
            "name": "Podish AltStore Repo",
            "identifier": "com.giantneko.podish-altstore",
            "apps": []
        }

    # Find or create app entry
    app_entry = next((a for a in data.get("apps", []) if a.get("bundleIdentifier") == bundle_id), None)
    if not app_entry:
        app_entry = {
            "name": app_name,
            "bundleIdentifier": bundle_id,
            "developerName": "GiantNeko",
            "localizedDescription": "x86 emulator for iOS",
            "iconURL": f"https://raw.githubusercontent.com/GiantNeko/{REPO_NAME}/main/icon.png",
            "versions": []
        }
        if "apps" not in data:
            data["apps"] = []
        data["apps"].append(app_entry)

    new_version_entry = {
        "version": full_version,
        "date": datetime.datetime.now().strftime("%Y-%m-%d"),
        "localizedDescription": f"Release {full_version}",
        "downloadURL": f"https://github.com/GiantNeko/{REPO_NAME}/releases/download/{tag}/{ipa_filename}",
        "size": os.path.getsize(ipa_path)
    }
    
    # Update app versions list (avoid duplicates)
    if "versions" not in app_entry:
        app_entry["versions"] = []
    
    # Remove existing entry for the same version if it exists
    app_entry["versions"] = [v for v in app_entry["versions"] if v["version"] != full_version]
    app_entry["versions"].insert(0, new_version_entry)
    
    # Update latest version info
    app_entry["version"] = full_version
    app_entry["versionDate"] = new_version_entry["date"]
    app_entry["versionDescription"] = new_version_entry["localizedDescription"]
    app_entry["downloadURL"] = new_version_entry["downloadURL"]
    app_entry["size"] = new_version_entry["size"]

    with open(apps_json_path, "w") as f:
        json.dump(data, f, indent=2)

    # Also copy apps.json to TEMP_DIR for release upload
    release_json_path = f"{TEMP_DIR}/apps.json"
    shutil.copy(apps_json_path, release_json_path)

    # 5. Commit and Push JSON & Icon to AltStore Repo (Must be done BEFORE release on empty repo)
    print("Committing and pushing to AltStore repo...")
    # Initialize if git status fails or if it's a new repo
    run(["git", "add", "apps.json", "icon.png"], cwd=altstore_repo_dir, check=False)
    run(["git", "add", "apps.json"], cwd=altstore_repo_dir)
    run(["git", "commit", "-m", f"Update to version {full_version}"], cwd=altstore_repo_dir, check=False)
    run(["git", "push", "origin", "main"], cwd=altstore_repo_dir)

    # 6. GH Release
    print(f"Creating/Uploading to GitHub Release {tag}...")
    # Create release (will not error if exists due to check=False)
    run(["gh", "release", "create", tag, "--repo", REPO_URL, "--title", f"Release {full_version}", "--notes", f"Automated release {full_version}"], check=False)
    # Upload IPA and JSON
    run(["gh", "release", "upload", tag, ipa_path, release_json_path, "--repo", REPO_URL, "--clobber"])

    print("\nSUCCESS!")
    print(f"IPA published: {new_version_entry['downloadURL']}")
    print(f"AltStore JSON updated in {REPO_URL}")


if __name__ == "__main__":
    main()
