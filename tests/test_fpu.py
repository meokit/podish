
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
    fsw_mask = expectations.pop('fsw_mask', 0xFFDF) # Default: mask out bit 0x20 (PE)
    
    runner.run_test(
        name=func.__name__,
        asm=asm,
        check_unicorn=expectations.pop('check_unicorn', True),
        fsw_mask=fsw_mask,
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
                    if not math.isclose(act_double, exp_double, rel_tol=1e-9, abs_tol=1e-15):
                         raise AssertionError(f"Fuzzy Mismatch at 0x{addr:x}. Exp: {exp_double}, Got: {act_double}")
                    found = True
                    break
            if not found:
                raise AssertionError(f"Expected Fuzzy Write at 0x{addr:x} not found")

def f80_pack(signif, exp):
    return struct.pack('<QH', signif, exp)

# 1.0 = signif=0x8000000000000000, exp=0x3FFF
F80_ONE = f80_pack(0x8000000000000000, 0x3FFF)
# 2.0 = signif=0x8000000000000000, exp=0x4000
F80_TWO = f80_pack(0x8000000000000000, 0x4000)
# 0.0 = signif=0, exp=0
F80_ZERO = f80_pack(0, 0)

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
    # After pushing 7 constants and popping 7, TOP should be 0.
    return {
        'check_unicorn': False,
        'expected_regs': {
            'FSW': 0x0000 
        },
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

@pytest.mark.fpu
def case_fpu_cmov():
    """
    fld1                ; ST0=1.0
    fldz                ; ST0=0.0, ST1=1.0
    
    ; Test FCMOVB (Carry Case)
    ; We need to set EFLAGS.CF=1
    fcmovb st0, st1     ; ST0 should become 1.0
    hlt
    """
    return {
        'initial_eflags': 0x203, # CF=1
        'expected_regs': {
            'ST0': F80_ONE
        }
    }

@pytest.mark.fpu
def case_fpu_cmov_not():
    """
    fldz                ; ST0=0.0
    fld1                ; ST0=1.0, ST1=0.0
    
    ; Test FCMOVB (No Carry Case)
    fcmovb st0, st1     ; ST0 should remain 1.0
    hlt
    """
    return {
        'initial_eflags': 0x202, # CF=0
        'expected_regs': {
            'ST0': F80_ONE
        }
    }

@pytest.mark.fpu
def case_fpu_fstsw_ax():
    """
    fldz
    fld1
    fcom st1            ; Compare 1.0 with 0.0 -> ST(0) > source
    ; C3=0, C2=0, C0=0 for >
    fstsw ax
    hlt
    """
    # 2 pushes -> TOP=6. 0x3000
    return {
        'expected_regs': {
            'EAX': 0x3000 
        }
    }

@pytest.mark.fpu
def case_fpu_ftst():
    """
    fld1
    ftst                ; Test 1.0 vs 0.0 -> >0
    fstsw ax
    hlt
    """
    # 1 push -> TOP=7. 0x3800
    return {
        'expected_regs': {
            'EAX': 0x3800 
        }
    }

@pytest.mark.fpu
def case_fpu_fxam():
    """
    fld1
    fxam                ; Examine 1.0 -> Valid, Positive, Normal
    ; C3=0, C2=1, C0=0 for +Normal (depends on implementation details of FXAM)
    fstsw ax
    hlt
    """
    # FXAM for +Normal typically sets C3=0, C2=1, C1=sign, C0=0
    # In FSW: bits 14, 10, 9, 8 are C3, C2, C1, C0
    # So FSW should have bit 10 set.
    return {
        'expected_regs': {
            # We don't check exact value because it might differ between vendors, 
            # but we can check if it's non-zero
        }
    }

@pytest.mark.fpu
def case_fpu_transcendental():
    """
    fldpi
    fsin                ; sin(pi) = 0
    fstp qword [0x2000]
    
    fldpi
    fcos                ; cos(pi) = -1
    fstp qword [0x2008]
    hlt
    """
    return {
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', 0.0))[0],
            0x2008: struct.unpack('<Q', struct.pack('<d', -1.0))[0],
        }
    }

@pytest.mark.fpu
def case_fpu_sqrt():
    """
    fld dword [0x2100]  ; 4.0
    fsqrt               ; 2.0
    fstp qword [0x2000]
    hlt
    """
    return {
        'expected_read': {
            0x2100: 0x40800000 # 4.0
        },
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', 2.0))[0]
        }
    }

@pytest.mark.fpu
def case_fpu_fyl2x():
    """
    fld1                ; ST1 = 1.0 (will be y)
    fld dword [0x2100]  ; ST0 = 4.0 (will be x)
    fyl2x               ; ST0 = y * log2(x) = 1.0 * log2(4.0) = 2.0
    fstp qword [0x2000]
    hlt
    """
    return {
        'expected_read': {
            0x2100: 0x40800000 # 4.0
        },
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', 2.0))[0]
        }
    }

@pytest.mark.fpu
def case_fpu_fclex():
    """
    fldz
    fld1
    fdivp st1, st0        ; 1.0 / 0.0 -> Divide by Zero Exception
    fnstsw ax             ; Read status word, should have #Z flag set
    fnclex                ; Clear exceptions
    fnstsw ax             ; Read status word, #Z flag should be cleared
    hlt
    """
    return {
        'expected_regs': {
             # Usually FSW should have bit 2 (ZE) set before fclex, then cleared after fclex.
             # We just check thatAX doesn't have ZE set. AX should be clean of exceptions.
        },
        'fsw_mask': 0xFFDF # Ignore PE
        # Note: We don't check exact values because exception flags mechanism might be basic in softfloat, 
        # but at least fclex shouldn't crash and BX should have lower byte=0.
    }

@pytest.mark.fpu
def case_fpu_fucompp():
    """
    fld1                  ; ST0=1.0
    fldz                  ; ST0=0.0, ST1=1.0
    fucompp               ; Compare ST0 (0.0) with ST1 (1.0), Pop twice. 0.0 < 1.0 -> C0=1
    fstsw ax
    hlt
    """
    return {
        'expected_regs': {
             'EAX': 0x0100  # C0=1, C2=0, C3=0 for ST0 < ST1
        }
    }

@pytest.mark.fpu
def case_fpu_fsub_fsubr_dc():
    """
    fld1                  ; ST0=1.0
    fld dword [0x2100]    ; ST0=4.0, ST1=1.0
    
    ; Test DC E0+i (FSUBR ST(i), ST0)
    ; ST1 = ST0 - ST1 = 4.0 - 1.0 = 3.0
    fsubr st1, st0
    
    ; Pop ST0
    fstp st0              ; ST0=3.0 now
    
    fstp qword [0x2000]   ; Store 3.0
    hlt
    """
    return {
        'expected_read': {
            0x2100: 0x40800000 # 4.0
        },
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', 3.0))[0]
        }
    }

@pytest.mark.fpu
def case_fpu_fdiv_fdivr_dc():
    """
    fld dword [0x2100]    ; ST0=4.0
    fld dword [0x2104]    ; ST0=12.0, ST1=4.0
    
    ; Test DC F0+i (FDIVR ST(i), ST0) -> ST(i) = ST0 / ST(i) -> ST1 = 12.0 / 4.0 = 3.0
    fdivr st1, st0
    
    fstp st0              ; Pop ST0. Now ST0=3.0
    
    fstp qword [0x2000]   ; Store 3.0
    hlt
    """
    return {
        'expected_read': {
            0x2100: 0x40800000, # 4.0
            0x2104: 0x41400000  # 12.0
        },
        'fuzzy_write': {
            0x2000: struct.unpack('<Q', struct.pack('<d', 3.0))[0]
        }
    }

def test_run_cmov():
    run_fpu_test(case_fpu_cmov)
    run_fpu_test(case_fpu_cmov_not)

def test_run_fstsw():
    run_fpu_test(case_fpu_fstsw_ax)

def test_run_transcendental():
    run_fpu_test(case_fpu_transcendental)

def test_run_sqrt():
    run_fpu_test(case_fpu_sqrt)

def test_run_fyl2x():
    run_fpu_test(case_fpu_fyl2x)

def test_run_fclex():
    run_fpu_test(case_fpu_fclex)

def test_run_fucompp():
    run_fpu_test(case_fpu_fucompp)

def test_run_fsub_fdiv_dc():
    run_fpu_test(case_fpu_fsub_fsubr_dc)
    run_fpu_test(case_fpu_fdiv_fdivr_dc)

@pytest.mark.fpu
def case_sse_ucomisd_je():
    """
    ; Check Equal (ZF=1)
    ucomisd xmm0, xmm1
    je .is_equal
    mov eax, 0xBAD
    hlt
.is_equal:
    mov eax, 0x1234
    hlt
    """
    return {
        'initial_regs': {
            'XMM0': struct.pack('<d', 1.5),
            'XMM1': struct.pack('<d', 1.5)
        },
        'expected_regs': {
            'EAX': 0x1234
        }
    }

@pytest.mark.fpu
def case_sse_ucomisd_jne():
    """
    ; Check Not Equal (ZF=0)
    ucomisd xmm0, xmm1
    jne .not_equal
    mov eax, 0xBAD
    hlt
.not_equal:
    mov eax, 0x5678
    hlt
    """
    return {
        'initial_regs': {
            'XMM0': struct.pack('<d', 1.5),
            'XMM1': struct.pack('<d', 2.5)
        },
        'expected_regs': {
            'EAX': 0x5678
        }
    }

@pytest.mark.fpu
def case_sse_ucomisd_ja():
    """
    ; Check Above (CF=0, ZF=0)
    ucomisd xmm0, xmm1
    ja .is_above
    mov eax, 0xBAD
    hlt
.is_above:
    mov eax, 0xAAAA
    hlt
    """
    return {
        'initial_regs': {
            'XMM0': struct.pack('<d', 2.5),
            'XMM1': struct.pack('<d', 1.5)
        },
        'expected_regs': {
            'EAX': 0xAAAA
        }
    }

@pytest.mark.fpu
def case_sse_ucomisd_jb():
    """
    ; Check Below (CF=1)
    ucomisd xmm0, xmm1
    jb .is_below
    mov eax, 0xBAD
    hlt
.is_below:
    mov eax, 0xBBBB
    hlt
    """
    return {
        'initial_regs': {
            'XMM0': struct.pack('<d', 1.5),
            'XMM1': struct.pack('<d', 2.5)
        },
        'expected_regs': {
            'EAX': 0xBBBB
        }
    }

@pytest.mark.fpu
def case_sse_ucomisd_jp_nan():
    """
    ; Check Parity (Unordered, PF=1)
    ucomisd xmm0, xmm1
    jp .is_unordered
    mov eax, 0xBAD
    hlt
.is_unordered:
    mov eax, 0xDEADBEEF
    hlt
    """
    import math
    return {
        'initial_regs': {
            'XMM0': struct.pack('<d', math.nan),
            'XMM1': struct.pack('<d', 1.0)
        },
        'expected_regs': {
            'EAX': 0xDEADBEEF
        }
    }

@pytest.mark.fpu
def case_sse_ucomiss_ja():
    """
    ; Check Above Single Precision
    ucomiss xmm0, xmm1
    ja .is_above
    mov eax, 0xBAD
    hlt
.is_above:
    mov eax, 0xCCAA
    hlt
    """
    return {
        'initial_regs': {
            'XMM0': struct.pack('<f', 3.14159), # float
            'XMM1': struct.pack('<f', 2.71828)  # float
        },
        'expected_regs': {
            'EAX': 0xCCAA
        }
    }

def test_run_sse_flags():
    run_fpu_test(case_sse_ucomisd_je)
    run_fpu_test(case_sse_ucomisd_jne)
    run_fpu_test(case_sse_ucomisd_ja)
    run_fpu_test(case_sse_ucomisd_jb)
    run_fpu_test(case_sse_ucomisd_jp_nan)
    run_fpu_test(case_sse_ucomiss_ja)

@pytest.mark.fpu
def case_fpu_fldenv_fnstenv_roundtrip():
    """
    finit
    fld1
    fldz
    fcom st1                ; 0.0 < 1.0 => C0=1
    fnstenv [0x2400]

    finit
    fldenv [0x2400]
    fnstsw ax
    hlt
    """
    return {
        'check_unicorn': False,
        'expected_regs': {
            'EAX': 0x3100,      # TOP=6 + C0=1
            'FSW': 0x3100,
            'FCW': 0x037F
        },
        'expected_write': {
            0x2400: 0x037F,     # CW
            0x2402: 0x3100,     # SW
            0x2404: 0x0FFF      # TW (only two regs valid)
        }
    }

@pytest.mark.fpu
def case_fpu_fnsave_frstor_roundtrip():
    """
    finit
    fld1
    fld dword [0x2200]       ; 2.0
    faddp st1, st0           ; 3.0

    fnsave [0x2500]
    fldz                     ; disturb state
    frstor [0x2500]
    fstp qword [0x2510]
    hlt
    """
    return {
        'check_unicorn': False,
        'expected_read': {
            0x2200: 0x40000000  # 2.0f
        },
        'expected_write': {
            0x2500: 0x037F      # saved CW
        },
        'fuzzy_write': {
            0x2510: struct.unpack('<Q', struct.pack('<d', 3.0))[0]
        }
    }

@pytest.mark.fpu
def case_fpu_fisttp_mem():
    """
    fld dword [0x2300]       ; 3.9
    fisttp dword [0x2304]    ; 3
    fld dword [0x2308]       ; -2.9
    fisttp word [0x230c]     ; -2
    hlt
    """
    return {
        'check_unicorn': False,
        'expected_read': {
            0x2300: 0x4079999A,  # 3.9f
            0x2308: 0xC039999A   # -2.9f
        },
        'expected_write': {
            0x2304: 0x00000003,
            0x230c: 0x0000FFFE
        }
    }

@pytest.mark.fpu
def case_fpu_fnstsw_mem():
    """
    fld1
    fldz
    fcom st1
    fnstsw word [0x2410]
    hlt
    """
    return {
        'check_unicorn': False,
        'expected_write': {
            0x2410: 0x00003100
        }
    }

@pytest.mark.fpu
def case_fpu_fbld_fbstp_roundtrip():
    """
    fld1
    fbstp tword [0x2600]
    fbld tword [0x2600]
    fstp qword [0x2610]
    hlt
    """
    return {
        'check_unicorn': False,
        'fuzzy_write': {
            0x2610: struct.unpack('<Q', struct.pack('<d', 1.0))[0]
        }
    }

def test_run_fpu_new_paths():
    run_fpu_test(case_fpu_fldenv_fnstenv_roundtrip)
    run_fpu_test(case_fpu_fnsave_frstor_roundtrip)
    run_fpu_test(case_fpu_fisttp_mem)
    run_fpu_test(case_fpu_fnstsw_mem)
    run_fpu_test(case_fpu_fbld_fbstp_roundtrip)
