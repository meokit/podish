#!/usr/bin/env python3
"""Generate a Podish OCI/tarfs store (image.json + indexes/rootfs.index.json) from a tar archive."""

import datetime
import hashlib
import json
import os
import sys
import tarfile

INODE_FILE = 0x8000
INODE_DIR = 0x4000
INODE_SYMLINK = 0xA000


def normalize_path(raw: str) -> str:
    p = raw.replace("\\", "/").strip()
    while p.startswith("./"):
        p = p[2:]
    p = p.lstrip("/")
    while "//" in p:
        p = p.replace("//", "/")
    if p in ("", "."):
        return "/"
    return "/" + p.rstrip("/")


def iso_utc(mtime: float) -> str:
    dt = datetime.datetime.fromtimestamp(max(0, int(mtime)), tz=datetime.timezone.utc)
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


def sha256_file(path: str) -> tuple[str, int]:
    h = hashlib.sha256()
    total = 0
    with open(path, "rb") as f:
        while True:
            chunk = f.read(1024 * 1024)
            if not chunk:
                break
            h.update(chunk)
            total += len(chunk)
    return f"sha256:{h.hexdigest()}", total


def build_store(tar_path: str, store_dir: str, image_ref: str) -> None:
    blobs_dir = os.path.join(store_dir, "blobs")
    indexes_dir = os.path.join(store_dir, "indexes")
    os.makedirs(blobs_dir, exist_ok=True)
    os.makedirs(indexes_dir, exist_ok=True)

    blob_rel = "blobs/rootfs.tar"
    index_rel = "indexes/rootfs.index.json"
    blob_dst = os.path.join(store_dir, blob_rel)
    index_dst = os.path.join(store_dir, index_rel)

    if os.path.abspath(tar_path) != os.path.abspath(blob_dst):
        os.replace(tar_path, blob_dst)
    os.chmod(blob_dst, 0o644)

    digest, blob_size = sha256_file(blob_dst)

    entries_by_path: dict[str, dict] = {
        "/": {
            "Path": "/",
            "Type": INODE_DIR,
            "Mode": 0o755,
            "Uid": 0,
            "Gid": 0,
            "Size": 0,
            "SymlinkTarget": "",
            "MTime": "1970-01-01T00:00:00Z",
            "ATime": "1970-01-01T00:00:00Z",
            "CTime": "1970-01-01T00:00:00Z",
            "InlineData": None,
            "DataOffset": -1,
            "BlobDigest": digest,
        }
    }
    pending_hardlinks: list[tuple[str, str, int, int, int, str]] = []

    with tarfile.open(blob_dst, mode="r:") as tf:
        for member in tf.getmembers():
            path = normalize_path(member.name)
            if path == "/":
                continue
            ts = iso_utc(member.mtime)
            mode = member.mode & 0o7777
            uid = int(member.uid)
            gid = int(member.gid)

            if member.isdir():
                entries_by_path[path] = {
                    "Path": path,
                    "Type": INODE_DIR,
                    "Mode": mode or 0o755,
                    "Uid": uid,
                    "Gid": gid,
                    "Size": 0,
                    "SymlinkTarget": "",
                    "MTime": ts,
                    "ATime": ts,
                    "CTime": ts,
                    "InlineData": None,
                    "DataOffset": -1,
                    "BlobDigest": digest,
                }
            elif member.issym():
                entries_by_path[path] = {
                    "Path": path,
                    "Type": INODE_SYMLINK,
                    "Mode": mode or 0o777,
                    "Uid": uid,
                    "Gid": gid,
                    "Size": 0,
                    "SymlinkTarget": member.linkname,
                    "MTime": ts,
                    "ATime": ts,
                    "CTime": ts,
                    "InlineData": None,
                    "DataOffset": -1,
                    "BlobDigest": digest,
                }
            elif member.islnk():
                target = normalize_path(member.linkname)
                pending_hardlinks.append((path, target, mode or 0o644, uid, gid, ts))
            elif member.isreg():
                entries_by_path[path] = {
                    "Path": path,
                    "Type": INODE_FILE,
                    "Mode": mode or 0o644,
                    "Uid": uid,
                    "Gid": gid,
                    "Size": int(member.size),
                    "SymlinkTarget": "",
                    "MTime": ts,
                    "ATime": ts,
                    "CTime": ts,
                    "InlineData": None,
                    "DataOffset": int(member.offset_data),
                    "BlobDigest": digest,
                }

    for path, target, mode, uid, gid, ts in pending_hardlinks:
        target_entry = entries_by_path.get(target)
        if target_entry is not None:
            cloned = dict(target_entry)
            cloned["Path"] = path
            entries_by_path[path] = cloned
        else:
            entries_by_path[path] = {
                "Path": path,
                "Type": INODE_SYMLINK,
                "Mode": mode,
                "Uid": uid,
                "Gid": gid,
                "Size": 0,
                "SymlinkTarget": target,
                "MTime": ts,
                "ATime": ts,
                "CTime": ts,
                "InlineData": None,
                "DataOffset": -1,
                "BlobDigest": digest,
            }

    sorted_entries = [entries_by_path[k] for k in sorted(entries_by_path.keys())]
    with open(index_dst, "w", encoding="utf-8") as f:
        json.dump(sorted_entries, f, separators=(",", ":"))

    image_meta = {
        "ImageReference": image_ref,
        "Registry": "local",
        "Repository": "podish/debian-lxqt",
        "Tag": "i386",
        "ManifestDigest": digest,
        "StoreDirectory": ".",
        "Layers": [
            {
                "Digest": digest,
                "MediaType": "application/vnd.oci.image.layer.v1.tar",
                "Size": blob_size,
                "BlobPath": blob_rel,
                "IndexPath": index_rel,
            }
        ],
        "ConfigUser": "user",
        "ConfigEntrypoint": None,
        "ConfigCmd": ["/usr/local/bin/podish-desktop-session"],
        "ConfigEnv": [
            "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            "LANG=C.UTF-8",
            "QT_QPA_PLATFORM=wayland",
            "QT_QPA_PLATFORMTHEME=lxqt",
            "XDG_CURRENT_DESKTOP=LXQt",
        ],
        "ConfigWorkingDir": "/home/user",
    }
    with open(os.path.join(store_dir, "image.json"), "w", encoding="utf-8") as f:
        json.dump(image_meta, f, indent=2)

    print(
        f"Indexed tarfs store at {store_dir}: {len(sorted_entries)} entries, "
        f"blob={blob_size / (1024 * 1024):.1f} MiB, index={os.path.getsize(index_dst) / (1024 * 1024):.2f} MiB"
    )


def main() -> int:
    if len(sys.argv) < 3:
        print(f"Usage: {sys.argv[0]} <rootfs.tar> <out-store-dir> [image-ref]", file=sys.stderr)
        return 2
    tar_path = sys.argv[1]
    store_dir = sys.argv[2]
    image_ref = sys.argv[3] if len(sys.argv) > 3 else "podish/debian-lxqt:i386"
    build_store(tar_path, store_dir, image_ref)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
