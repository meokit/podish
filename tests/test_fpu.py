
import pytest
from tests.runner import Runner
import struct

def run_fpu_test(func):
    """
    Helper to run test using docstring as assembly.
    """
    runner = Runner()
    asm = func.__doc__
    if not asm:
        raise ValueError(f"No docstring found for {func.__name__}")
    
    # Defaults or extracted args
    expectations = func()
    
    # Extract fuzzy check requirements
    fuzzy_write = expectations.pop('fuzzy_write', None)
    
    runner.run_test(
        name=func.__name__,
        asm=asm,
        **expectations
    )
    
    if fuzzy_write:
        for addr, expected_val in fuzzy_write.items():
            # Find write in trace
            found = False
            for op, t_addr, t_val, t_size in runner.sim_trace:
                if op == 'W' and t_addr == addr:
                    # Convert t_val (int) to double
                    act_bytes = t_val.to_bytes(8, 'little')
                    act_double = struct.unpack('<d', act_bytes)[0]
                    
                    exp_bytes = expected_val.to_bytes(8, 'little')
                    exp_double = struct.unpack('<d', exp_bytes)[0]
                    
                    import math
                    if not math.isclose(act_double, exp_double, rel_tol=1e-9):
                         raise AssertionError(f"Fuzzy Mismatch at 0x{addr:x}. Exp: {exp_double}, Got: {act_double}")
                    found = True
                    break
            if not found:
                raise AssertionError(f"Expected Fuzzy Write at 0x{addr:x} not found")

@pytest.mark.fpu
def case_fpu_constants():
    """
    fldpi
    fldl2e
    fldl2t
    fldlg2
    fldln2
    fld1
    fldz
    
    ; Pop check (reverse order)
    fstp qword [0x2000] ; 0.0 (FLDZ)
    fstp qword [0x2008] ; 1.0 (FLD1)
    fstp qword [0x2010] ; ln2
    fstp qword [0x2018] ; log10(2)
    fstp qword [0x2020] ; log2(10)
    fstp qword [0x2028] ; log2(e)
    fstp qword [0x2030] ; pi
    hlt
    """
    import math
    # Use fuzzy_write for imprecise constants
    return {
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', 0.0))[0],
            0x2008: struct.unpack('<Q', struct.pack('<d', 1.0))[0],
            0x2010: struct.unpack('<Q', struct.pack('<d', math.log(2)))[0],
            0x2018: struct.unpack('<Q', struct.pack('<d', math.log10(2)))[0],
            0x2020: struct.unpack('<Q', struct.pack('<d', math.log2(10)))[0],
            0x2028: struct.unpack('<Q', struct.pack('<d', math.log2(math.e)))[0],
            0x2030: struct.unpack('<Q', struct.pack('<d', math.pi))[0],
        }
    }

@pytest.mark.fpu
def case_fpu_arithmetic():
    """
    fld1                ; 1.0
    fld1                ; 1.0, 1.0
    faddp st1, st0      ; 2.0
    
    fldpi               ; PI, 2.0
    fmulp st1, st0      ; 2.0 * PI
    
    ; Load 4.0 from memory (Address 0x2100 fixed)
    fld dword [0x2100]  ; 4.0, 2PI
    fdivp st1, st0      ; 2PI / 4.0 = PI / 2
    
    fstp qword [0x2000] ; Store PI/2
    hlt
    """
    import math
    
    # 4.0 as float (0x40800000)
    return {
        'expected_read': {
            0x2100: 0x40800000 # 4.0 float
        },
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', math.pi / 2))[0]
        }
    }

@pytest.mark.fpu
def case_fpu_stack_ops():
    """
    fld1                ; 1.0           ST0=1.0
    fldpi               ; PI, 1.0       ST0=PI, ST1=1.0
    fxch st1            ; 1.0, PI       ST0=1.0, ST1=PI
    fsubp st1, st0      ; PI - 1.0      ST0=PI-1.0
    fstp qword [0x2000] ; Store
    hlt
    """
    import math
    return {
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', math.pi - 1.0))[0]
        }
    }

def test_run_constants():
    try:
        run_fpu_test(case_fpu_constants)
    except AssertionError as e:
        print(e)
        raise

def test_run_arithmetic():
    try:
        run_fpu_test(case_fpu_arithmetic)
    except AssertionError as e:
        print(e)
        raise

def test_run_stack_ops():
    try:
        run_fpu_test(case_fpu_stack_ops)
    except AssertionError as e:
        print(e)
        raise
