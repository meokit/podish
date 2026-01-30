# x86emu Test Suite

This directory contains the test suite for the x86 IA-32 emulator.

## Test Structure

### Unit Tests
- `test_hook_verify.py` - Memory hook and EFLAGS verification
- `test_seg_base.py` - Segment base support tests

### Regression Tests
- `regression/test_redis_*.py` - Auto-generated tests from Redis binary instructions
  - Each instruction is a separate pytest test function
  - Tests are marked with `@pytest.mark.regression`

## Running Tests

### Run All Tests
```bash
pytest
```

### Run Specific Test Categories
```bash
# Only unit tests
pytest -m unit

# Only regression tests
pytest -m regression

# Run a specific test file
pytest tests/test_hook_verify.py

# Run a specific test function
pytest tests/regression/test_redis_000.py::test_id_170_adc_m32_imm32
```

### Verbose Output
```bash
pytest -v
```

### Show All Test Output (including passed tests)
```bash
pytest -v -s
```

### Stop on First Failure
```bash
pytest -x
```

## Test Generation

Regression tests are generated from the Redis binary instruction samples:

```bash
python3 tests/gen_regression.py
```

This will:
1. Read unique instructions from `analyze/instructions.db`
2. Deduplicate and sort them
3. Generate `analyze/instructions.md` documentation
4. Generate individual pytest test functions in `tests/regression/test_redis_*.py`

## Writing Tests

Use the `TestRunner` class for all tests:

```python
from tests.runner import TestRunner
import binascii
import pytest

@pytest.mark.unit
def test_example():
    runner = TestRunner()
    
    assert runner.run_test_bytes(
        name="Example Test",
        code=binascii.unhexlify("89C3"),  # MOV EBX, EAX
        initial_regs={'EAX': 0x12345678},
        expected_regs={'EBX': 0x12345678},
        initial_eflags=0,
        expected_eflags=0,
        expected_eip=0x1002,
        expected_read={},
        expected_write={},
        initial_seg_base=None
    )
```

## Test Parameters

- `initial_regs`: Dict of register initial values (e.g., `{'EAX': 0x100}`)
- `expected_regs`: Dict of expected register values after execution
- `initial_eflags`: Initial EFLAGS value (default: 0x202)
- `expected_eflags`: Expected EFLAGS value after execution
- `expected_eip`: Expected instruction pointer after execution
- `expected_read`: Dict of expected memory reads `{addr: value}`
- `expected_write`: Dict of expected memory writes `{addr: value}`
- `initial_seg_base`: List of 6 segment bases `[ES, CS, SS, DS, FS, GS]` (default: all zeros)
