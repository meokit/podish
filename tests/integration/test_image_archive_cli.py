from __future__ import annotations

import subprocess
import tarfile
from pathlib import Path

import pytest


def _run_fiberpod_cli(project_root: Path, *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
            "--no-build",
            "--",
            *args,
        ],
        cwd=str(project_root),
        capture_output=True,
        text=True,
    )

def _run_fiberpod_cli_bin(project_root: Path, *args: str) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
            "--no-build",
            "--",
            *args,
        ],
        cwd=str(project_root),
        capture_output=True,
        text=False,
    )


def test_save_load_roundtrip_contract(project_root: Path, fiberpod_dll: str, alpine_image: str, tmp_path: Path) -> None:
    _ = fiberpod_dll  # force build fixture
    _ = alpine_image  # ensure test image exists in local OCI store

    archive = tmp_path / "alpine-image.tar"

    save_res = _run_fiberpod_cli(
        project_root,
        "save",
        "-o",
        str(archive),
        "docker.io/i386/alpine:latest",
    )
    assert save_res.returncode == 0, f"save failed\nstdout:\n{save_res.stdout}\nstderr:\n{save_res.stderr}"
    assert archive.exists() and archive.stat().st_size > 0, "save did not produce archive"
    with tarfile.open(archive, "r") as tf:
        names = set(tf.getnames())
    assert "oci-layout" in names, "save archive is not OCI layout"
    assert "index.json" in names, "save archive missing OCI index.json"

    safe_image = "docker.io_i386_alpine_latest"
    store_dir = project_root / ".fiberpod" / "oci" / "images" / safe_image
    load_res = _run_fiberpod_cli(
        project_root,
        "load",
        "-i",
        str(archive),
    )
    assert load_res.returncode == 0, f"load failed\nstdout:\n{load_res.stdout}\nstderr:\n{load_res.stderr}"
    assert (store_dir / "image.json").exists(), "load did not preserve image metadata"


def test_import_contract(project_root: Path, fiberpod_dll: str, tmp_path: Path) -> None:
    _ = fiberpod_dll  # force build fixture

    rootfs_tar = tmp_path / "mini-rootfs.tar"
    payload_file = tmp_path / "hello.txt"
    payload_file.write_text("hello-from-import\n", encoding="utf-8")
    with tarfile.open(rootfs_tar, "w") as tf:
        tf.add(payload_file, arcname="hello.txt")

    import_res = _run_fiberpod_cli(
        project_root,
        "import",
        str(rootfs_tar),
        "localhost/fiberpod-import:test",
    )
    assert import_res.returncode == 0, (
        f"import failed\nstdout:\n{import_res.stdout}\nstderr:\n{import_res.stderr}"
    )

    store_dir = project_root / ".fiberpod" / "oci" / "images" / "localhost_fiberpod-import_test"
    image_json = store_dir / "image.json"
    assert image_json.exists(), "import did not create image metadata"


def test_import_gzip_contract(project_root: Path, fiberpod_dll: str, tmp_path: Path) -> None:
    _ = fiberpod_dll  # force build fixture

    rootfs_tar = tmp_path / "mini-rootfs-gz.tar"
    payload_file = tmp_path / "hello-gz.txt"
    payload_file.write_text("hello-from-import-gzip\n", encoding="utf-8")
    with tarfile.open(rootfs_tar, "w") as tf:
        tf.add(payload_file, arcname="hello-gz.txt")

    rootfs_targz = tmp_path / "mini-rootfs-gz.tar.gz"
    with open(rootfs_tar, "rb") as src, open(rootfs_targz, "wb") as dst:
        import gzip
        with gzip.GzipFile(fileobj=dst, mode="wb") as gz:
            gz.write(src.read())

    import_res = _run_fiberpod_cli(
        project_root,
        "import",
        str(rootfs_targz),
        "localhost/fiberpod-import:gzip",
    )
    assert import_res.returncode == 0, (
        f"import gzip failed\nstdout:\n{import_res.stdout}\nstderr:\n{import_res.stderr}"
    )
    store_dir = project_root / ".fiberpod" / "oci" / "images" / "localhost_fiberpod-import_gzip"
    assert (store_dir / "image.json").exists(), "gzip import did not create image metadata"


def test_export_contract(project_root: Path, fiberpod_dll: str, alpine_image: str, tmp_path: Path) -> None:
    _ = fiberpod_dll  # force build fixture
    _ = alpine_image

    containers_dir = project_root / ".fiberpod" / "containers"
    containers_dir.mkdir(parents=True, exist_ok=True)
    before = {p.name for p in containers_dir.iterdir() if p.is_dir()}
    run_res = _run_fiberpod_cli(
        project_root,
        "run",
        "docker.io/i386/alpine:latest",
        "--",
        "/bin/sh",
        "-c",
        "echo upper-export-ok > /tmp/fiberpod-export-marker.txt",
    )
    assert run_res.returncode == 0, f"run failed\nstdout:\n{run_res.stdout}\nstderr:\n{run_res.stderr}"
    after = {p.name for p in containers_dir.iterdir() if p.is_dir()}
    created = sorted(after - before)
    assert created, "no container directory created by run"
    container_id = created[-1]
    try:
        archive = tmp_path / "container-export.tar"
        export_res = _run_fiberpod_cli(
            project_root,
            "export",
            "-o",
            str(archive),
            container_id,
        )
        assert export_res.returncode == 0, (
            f"export failed\nstdout:\n{export_res.stdout}\nstderr:\n{export_res.stderr}"
        )
        assert archive.exists() and archive.stat().st_size > 0, "export did not produce archive"

        with tarfile.open(archive, "r") as tf:
            names = set(tf.getnames())
            marker = tf.extractfile("tmp/fiberpod-export-marker.txt")
            marker_text = marker.read().decode("utf-8").strip() if marker is not None else ""
        assert "bin/sh" in names or "./bin/sh" in names, "export archive missing expected rootfs content"
        assert marker_text == "upper-export-ok"

        export_stdout_res = _run_fiberpod_cli_bin(
            project_root,
            "export",
            container_id,
        )
        assert export_stdout_res.returncode == 0, (
            f"export stdout failed\nstdout bytes={len(export_stdout_res.stdout)}\nstderr:\n"
            f"{export_stdout_res.stderr.decode('utf-8', errors='replace')}"
        )
        stdout_archive = tmp_path / "container-export-stdout.tar"
        stdout_archive.write_bytes(export_stdout_res.stdout)
        with tarfile.open(stdout_archive, "r") as tf:
            names_stdout = set(tf.getnames())
        assert "tmp/fiberpod-export-marker.txt" in names_stdout
    finally:
        rm_res = _run_fiberpod_cli(project_root, "rm", "-f", container_id)
        assert rm_res.returncode == 0, (
            f"cleanup rm failed\nstdout:\n{rm_res.stdout}\nstderr:\n{rm_res.stderr}"
        )
