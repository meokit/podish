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


class HandlerSignatureTests(unittest.TestCase):
    def test_windows_reflection_includes_handler_without_restrict(self):
        args = ("struct fiberish::EmuState *, struct fiberish::DecodedOp *, __int64, "
                "unsigned __int64, unsigned __int64, unsigned int, unsigned __int64")
        template = f"__int64 fiberish::MemoryOpRestart({args.replace('*', '*__restrict')})"
        identity = f"__int64 fiberish::DispatchWrapper<&fiberish::op::OpAdd_EbGb>({args})"
        wrong_args = args.replace("unsigned int,", "unsigned short,")
        wrong_handler = f"__int64 fiberish::WrongHandler({wrong_args})"
        symbols = [("?template", template), ("?identity", identity), ("?wrong", wrong_handler)]
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "reflection.cpp"
            argv = ["gen_handler_reflection.py", "--nm", "llvm-nm", "--output", str(output), "host.obj"]
            with mock.patch.object(reflection.sys, "argv", argv), \
                 mock.patch.object(reflection, "run_nm", return_value=symbols):
                self.assertEqual(0, reflection.main())
            generated = output.read_text(encoding="utf-8")
        self.assertIn(f'"{identity}"', generated)
        self.assertIn(f'"{template}"', generated)
        self.assertNotIn(f'"{wrong_handler}"', generated)


if __name__ == "__main__":
    unittest.main()
