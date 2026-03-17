# x86emu Python Tests

This directory contains the Python-driven test and regression tooling for the emulator core.

These tests are separate from the managed test projects:

- `Fiberish.Tests/`
- `Fiberish.SilkFS.Tests/`

Use this directory when you want instruction-level verification, regression generation, or the Python integration harnesses.

## Layout

### Unit tests

- `test_hook_verify.py`: memory hook and EFLAGS verification
- `test_seg_base.py`: segment base behavior

### Regression tests

- `regression/test_redis_*.py`: auto-generated instruction tests from sampled Redis instructions
- each instruction becomes one pytest test
- regression cases are marked with `@pytest.mark.regression`

## Running pytest

Run all Python tests:

```bash
pytest
```

Run only unit tests:

```bash
pytest -m unit
```

Run only regression tests:

```bash
pytest -m regression
```

Run one file:

```bash
pytest tests/test_hook_verify.py
```

Run one test:

```bash
pytest tests/regression/test_redis_000.py::test_id_170_adc_m32_imm32
```

Useful flags:

```bash
pytest -v
pytest -v -s
pytest -x
```

## Generating regression tests

Regression tests are generated from instruction samples stored in `analyze/instructions.db`:

```bash
python3 tests/gen_regression.py
```

This script:

1. Reads unique instructions from `analyze/instructions.db`
2. Deduplicates and sorts them
3. Regenerates `analyze/instructions.md`
4. Regenerates `tests/regression/test_redis_*.py`

## Writing tests

Use `tests.runner.TestRunner` for instruction-level checks:

```python
from tests.runner import TestRunner
import binascii
import pytest

@pytest.mark.unit
def test_example():
    runner = TestRunner()

    assert runner.run_test_bytes(
        name="Example Test",
        code=binascii.unhexlify("89C3"),
        initial_regs={"EAX": 0x12345678},
        expected_regs={"EBX": 0x12345678},
        initial_eflags=0,
        expected_eflags=0,
        expected_eip=0x1002,
        expected_read={},
        expected_write={},
        initial_seg_base=None,
    )
```

## Common parameters

- `initial_regs`: initial register values, for example `{"EAX": 0x100}`
- `expected_regs`: expected register values after execution
- `initial_eflags`: initial EFLAGS value, default is usually `0x202`
- `expected_eflags`: expected EFLAGS value after execution
- `expected_eip`: expected guest EIP after execution
- `expected_read`: expected memory reads as `{addr: value}`
- `expected_write`: expected memory writes as `{addr: value}`
- `initial_seg_base`: six segment bases `[ES, CS, SS, DS, FS, GS]`
