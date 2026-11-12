"""Host-only tests: python -m unittest discover -s tests/ltp -p 'test_*.py'."""
import sys
import tempfile
import unittest
from pathlib import Path

from run import classify, execute


def summary(passed=1, failed=0, broken=0, skipped=0, warnings=0):
    return (f"Summary:\npassed   {passed}\nfailed   {failed}\nbroken   {broken}\n"
            f"skipped  {skipped}\nwarnings {warnings}\n")


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


if __name__ == "__main__":
    unittest.main()
