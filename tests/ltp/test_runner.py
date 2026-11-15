"""Host-only tests: python -m unittest discover -s tests/ltp -p 'test_*.py'."""
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import io
import json
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import run
from run import classify, execute


def summary(passed=1, failed=0, broken=0, skipped=0, warnings=0):
    return (f"Summary:\npassed   {passed}\nfailed   {failed}\nbroken   {broken}\n"
            f"skipped  {skipped}\nwarnings {warnings}\n")


class SourceStateTests(unittest.TestCase):
    def test_diff_digest_preserves_invalid_utf8_bytes(self):
        digests = []
        for diff in (b"\xff", b"\xfe"):
            with patch.object(run.subprocess, "check_output", side_effect=["pinned\n", diff, ""]):
                state = run.source_state()
            self.assertTrue(state["dirty"])
            digests.append(state["changesSha256"])
        self.assertNotEqual(*digests)

    def test_untracked_quoted_and_multiline_names_are_hashed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            name = 'space and "quote".txt' if sys.platform != "win32" else "space and unicode-文.txt"
            (root / name).write_bytes(b"first")
            with patch.object(run, "REPO", root), patch.object(run.subprocess, "check_output",
                    side_effect=["pinned\n", b"", name + "\0"]):
                first = run.source_state()
            (root / name).write_bytes(b"second")
            with patch.object(run, "REPO", root), patch.object(run.subprocess, "check_output",
                    side_effect=["pinned\n", b"", name + "\0"]):
                second = run.source_state()
            self.assertTrue(first["dirty"])
            self.assertNotEqual(first["changesSha256"], second["changesSha256"])


class BusyboxImageTests(unittest.TestCase):
    def test_applet_list_rejects_paths_and_duplicates(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "applets.txt"
            path.write_text("sh\ncp\n")
            self.assertEqual(run.read_applets(path), ["sh", "cp"])
            path.write_text("sh\n../cp\n")
            with self.assertRaises(SystemExit):
                run.read_applets(path)
            path.write_text("sh\nsh\n")
            with self.assertRaises(SystemExit):
                run.read_applets(path)

    def test_busybox_symlinks_cover_bin_and_sbin(self):
        links = dict(run.busybox_symlinks(["sh", "ip", "definitely-not-an-applet"]))
        self.assertEqual(links["bin/sh"], "busybox")
        self.assertEqual(links["sbin/ip"], "../bin/busybox")
        self.assertNotIn("sbin/definitely-not-an-applet", links)

    def test_image_tar_contains_symlinks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "rootfs"
            (root / "bin").mkdir(parents=True)
            (root / "bin/busybox").write_bytes(b"busybox")
            tar_path = Path(directory) / "image.tar"
            run.build_image_tar(root, tar_path, {"bin/busybox"},
                                [("bin/sh", "busybox"), ("sbin/ip", "../bin/busybox")])
            with tarfile.open(tar_path) as archive:
                shell = archive.getmember("bin/sh")
                ip = archive.getmember("sbin/ip")
                self.assertTrue(shell.issym())
                self.assertEqual(shell.linkname, "busybox")
                self.assertTrue(ip.issym())
                self.assertEqual(ip.linkname, "../bin/busybox")
                self.assertEqual(archive.getmember("bin/busybox").mode, 0o755)


class ResultTests(unittest.TestCase):
    def test_pass_requires_assertions_and_summary(self):
        self.assertEqual(classify(0, summary())[0], "PASS")
        self.assertEqual(classify(0, "hello")[0], "BROKEN")
        self.assertEqual(classify(0, summary(passed=0))[0], "BROKEN")
        self.assertEqual(classify(1, "Podish failed to start")[0], "BROKEN")

    def test_failure_bitmask_and_assertions(self):
        self.assertEqual(classify(1, summary(passed=0, failed=1))[0], "FAIL")
        self.assertEqual(classify(0, summary(failed=1))[0], "FAIL")
        self.assertEqual(classify(33, summary(failed=1, skipped=1))[0], "FAIL")

    def test_configuration_skip_is_not_success(self):
        self.assertEqual(classify(32, summary(passed=0, skipped=1))[0], "SKIP")

    def test_supported_variants_with_extra_skip_are_a_pass_before_baseline_compare(self):
        self.assertEqual(classify(32, summary(passed=2, skipped=1))[0], "PASS")

    def test_broken_and_crash_are_not_syscall_failures(self):
        self.assertEqual(classify(2, summary(broken=1))[0], "BROKEN")
        self.assertEqual(classify(-11, "")[0], "BROKEN")
        self.assertEqual(classify(137, "")[0], "BROKEN")

    def test_timeout_takes_precedence_over_partial_passes(self):
        self.assertEqual(classify(-9, summary(), timed_out=True)[0], "TIMEOUT")

    def test_all_variants_are_counted(self):
        status, counts = classify(0, summary(passed=2) + summary(passed=3))
        self.assertEqual(status, "PASS")
        self.assertEqual(counts["passed"], 5)
        self.assertEqual(classify(0, summary(failed=1) + summary())[0], "FAIL")

    def test_cleanup_warning_after_summary_is_not_pass(self):
        self.assertEqual(classify(0, summary() + "tst_tmpdir.c:1: TWARN: cleanup failed\n")[0], "BROKEN")

    def test_partial_skips_are_preserved(self):
        status, counts = classify(0, summary(passed=2, skipped=1))
        self.assertEqual(status, "PASS")
        self.assertEqual(counts["skipped"], 1)

    def test_malformed_summary_is_broken(self):
        self.assertEqual(classify(0, "Summary:\npassed 9\n")[0], "BROKEN")

    def test_legacy_ltp_result_lines_are_counted(self):
        text = "getgroups01    1  TPASS  :  ok\ngetgroups01    2  TPASS  :  ok\n"
        status, counts = classify(0, text)
        self.assertEqual(status, "PASS")
        self.assertEqual(counts["passed"], 2)
        self.assertEqual(classify(1, "x 1  TFAIL  :  bad\n")[0], "FAIL")
        self.assertEqual(classify(0, "silent legacy binary")[0], "BROKEN")
        self.assertEqual(classify(0, "Child process returned TPASS\n")[0], "PASS")


class ProcessTests(unittest.TestCase):
    def test_output_and_status_are_preserved(self):
        with tempfile.TemporaryDirectory() as work:
            code, text, expired, _ = execute(
                [sys.executable, "-c", "print('output'); raise SystemExit(3)"],
                Path(work), None, 5)
        self.assertEqual(code, 3)
        self.assertIn("output", text)
        self.assertFalse(expired)

    def test_hung_process_is_terminated(self):
        with tempfile.TemporaryDirectory() as work:
            _, _, expired, elapsed = execute(
                [sys.executable, "-c", "import time; time.sleep(60)"], Path(work), None, 0.1)
        self.assertTrue(expired)
        self.assertLess(elapsed, 10)


class BackendRoutingTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="ltp-runner-test-")
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.artifacts = self.root / "artifacts"
        (self.artifacts / "bin").mkdir(parents=True)
        (self.artifacts / "results").mkdir()
        self.binary = self.artifacts / "bin" / "mmap01"
        self.binary.write_bytes(b"mock executable, never launched")
        self.cli = self.root / "cli.dll"
        self.cli.touch()
        self.manifest = self.root / "cases.json"
        self.manifest.write_text(json.dumps({"cases": [{"name": "mmap01", "group": "mmap"}]}))
        self.metadata = {"ltpCommit": "pinned", "manifest": "cases.json",
                         "binaries": {"mmap01": hashlib.sha256(self.binary.read_bytes()).hexdigest()}}
        (self.artifacts / "build.json").write_text(json.dumps(self.metadata))
        (self.artifacts / "results/native.json").write_text(json.dumps({
            "backend": "native", "build": self.metadata,
            "cases": [{"name": "mmap01", "status": "PASS",
                       "counts": dict(passed=1, failed=0, broken=0, skipped=0, warnings=0)}]}))
        self.commands = []

    def invoke(self, backend=None, text=None, code=0, extra=()):
        argv = ["run.py", "--backend", "podish", "--artifacts", str(self.artifacts),
                "--manifest", str(self.manifest), "--cli", str(self.cli)]
        if backend:
            argv += ["--fs-backend", backend]
        argv += list(extra)

        def fake_execute(command, cwd, env, timeout):
            self.commands.append((command, cwd, timeout))
            return (0, "", False, .01) if "import" in command else (code, text or summary(), False, .01)

        def fake_git_output(command, **kwargs):
            if "diff" in command:
                return b""
            if "ls-files" in command:
                return ""
            return "pinned\n"

        with patch.object(sys, "argv", argv), patch.object(run, "execute", side_effect=fake_execute), \
                patch.object(run.subprocess, "check_output", side_effect=fake_git_output), \
                redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            return run.main()

    def report(self, backend):
        return json.loads((self.artifacts / "results" / f"podish-{backend}.json").read_text())

    def test_silkfs_import_and_execution_select_requested_backend(self):
        self.assertEqual(self.invoke("silkfs"), 0)
        self.assertEqual(len(self.commands), 2)
        self.assertIn("import", self.commands[0][0])
        command, cwd, timeout = self.commands[1]
        self.assertEqual(command[command.index("--fs-backend") + 1], "silkfs")
        self.assertNotIn("--rootfs", command)
        self.assertEqual(cwd.name, "image-store")
        self.assertEqual(timeout, 30)
        self.assertEqual(self.report("silkfs")["fileSystemBackend"], "silkfs")

    def test_tmpfs_and_hostfs_routing_stay_distinct(self):
        self.assertEqual(self.invoke("tmpfs"), 0)
        self.assertIn("tmpfs", self.commands[-1][0])
        self.assertEqual(self.invoke("hostfs"), 0)
        self.assertIn("--rootfs", self.commands[-1][0])
        self.assertNotIn("--fs-backend", self.commands[-1][0])
        self.assertEqual(self.report("tmpfs")["fileSystemBackend"], "tmpfs")
        self.assertEqual(self.report("hostfs")["fileSystemBackend"], "hostfs")

    def test_manifest_can_select_silkfs_without_override(self):
        data = json.loads(self.manifest.read_text())
        data["podishFsBackend"] = "silkfs"
        self.manifest.write_text(json.dumps(data))
        self.assertEqual(self.invoke(), 0)
        self.assertEqual(self.report("silkfs")["fileSystemBackend"], "silkfs")

    def test_explicit_output_is_preserved_and_no_provenance_is_invented(self):
        output = self.root / "chosen.json"
        self.assertEqual(self.invoke("hostfs", extra=["--output", str(output)]), 0)
        report = json.loads(output.read_text())
        self.assertEqual(report["dataBackendEvidence"]["status"], "not-collected")
        self.assertFalse(report["dataBackendEvidence"]["directMappingVerified"])

    def test_missing_native_pass_and_binary_hash_still_block_execution(self):
        self.binary.write_bytes(b"tampered")
        with self.assertRaises(SystemExit):
            self.invoke("silkfs")
        self.assertEqual(self.commands, [])

    def test_partial_and_failure_results_remain_nonzero(self):
        self.assertEqual(self.invoke("silkfs", text=summary(passed=1, skipped=1), code=32), 1)
        self.assertEqual(self.report("silkfs")["cases"][0]["status"], "PARTIAL")
        self.assertEqual(self.invoke("hostfs", text=summary(failed=1), code=1), 1)
        self.assertEqual(self.report("hostfs")["cases"][0]["status"], "FAIL")

    def test_trace_fresh_container_and_guest_environment_are_preserved(self):
        self.assertEqual(self.invoke("silkfs", extra=["--trace"]), 0)
        command = self.commands[-1][0]
        for option in ("--rm", "--init", "--strace", "--log-file"):
            self.assertIn(option, command)
        self.assertIn("TMPDIR=/tmp", command)
        self.assertTrue(command[-1].endswith("/mmap01"))


if __name__ == "__main__":
    unittest.main()
