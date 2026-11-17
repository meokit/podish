import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock


spec = importlib.util.spec_from_file_location(
    "gen_handler_reflection", Path(__file__).resolve().parents[1] / "gen_handler_reflection.py"
)
reflection = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reflection)


class SymbolToolSelectionTests(unittest.TestCase):
    def test_explicit_compiler_sibling_wins_over_other_llvm_on_path(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp) / "selected compiler with spaces"
            directory.mkdir()
            compiler = directory / ("clang-cl.exe" if os.name == "nt" else "clang++")
            sibling = directory / ("llvm-nm.exe" if os.name == "nt" else "llvm-nm")
            compiler.touch()
            sibling.touch()
            def which(name):
                return str(compiler) if name == str(compiler) else "wrong-llvm-nm"

            with mock.patch.object(reflection.shutil, "which", side_effect=which):
                self.assertEqual(str(sibling.resolve()), reflection.find_tool("llvm-nm", compiler=str(compiler)))

    def test_missing_sibling_keeps_path_fallback(self):
        with tempfile.TemporaryDirectory() as temp:
            compiler = Path(temp) / "clang"
            compiler.touch()
            with mock.patch.object(reflection.shutil, "which", side_effect=lambda name: None if name == str(compiler) else "path-nm"):
                self.assertEqual("path-nm", reflection.find_tool("llvm-nm", compiler=str(compiler)))

    def test_no_explicit_compiler_retains_existing_path_selection(self):
        with mock.patch.object(reflection.shutil, "which", return_value="path-nm"):
            self.assertEqual("path-nm", reflection.find_tool("llvm-nm"))

    def test_explicit_nm_overrides_compiler_and_path_discovery(self):
        argv = ["gen_handler_reflection.py", "--nm", "xcode/nm", "--compiler", "other/clang",
                "--output", "unused.cpp", "host.o"]
        # Stop at the first scan: this regression checks actual CLI precedence
        # without requiring a platform-specific object file or symbol reader.
        with mock.patch.object(reflection.sys, "argv", argv), \
             mock.patch.object(reflection, "find_tool") as discover, \
             mock.patch.object(reflection, "run_nm", side_effect=RuntimeError("scan reached")):
            with self.assertRaisesRegex(RuntimeError, "scan reached"):
                reflection.main()
            self.assertEqual("xcode/nm", reflection.NM_TOOL)
            discover.assert_not_called()

    def test_symbol_tool_failure_includes_llvm_diagnostic(self):
        failure = subprocess.CompletedProcess(["llvm-nm", "module.obj"], 1, "", "Unknown attribute kind in bitcode\n")
        with mock.patch.object(reflection.subprocess, "run", return_value=failure):
            with self.assertRaisesRegex(RuntimeError, "Unknown attribute kind in bitcode"):
                reflection.run_tool(["llvm-nm", "module.obj"])


if __name__ == "__main__":
    unittest.main()
