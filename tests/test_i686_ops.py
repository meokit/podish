
import pytest
from tests.runner import Runner

def run_test(func, check_unicorn=True):
    runner = Runner()
    asm = func.__doc__
    if not asm:
        raise ValueError(f"No docstring found for {func.__name__}")
    expectations = func()
    runner.run_test(
        name=func.__name__,
        asm=asm,
        check_unicorn=check_unicorn,
        **expectations
    )

# =============================================================================
# String Operations (LODS, SCAS, CMPS)
# =============================================================================

@pytest.mark.i686
def case_lods_rep():
    """
    ; ESI = 0x2000 (Data)
    ; ECX = 4
    ; Read 4 bytes into EAX sequentially?
    ; LODS loads DS:ESI into accumulator.
    ; With REP, it keeps loading and overwriting EAX.
    ; Last byte should remain.
    mov esi, 0x2000
    mov ecx, 3
    mov eax, 0
    rep lodsb
    hlt
    """
    # 0x2000 contains 0x11, 0x22, 0x33, 0x44 (Initialized below)
    return {
        'expected_regs': {
            'EAX': 0x33, # Last byte loaded
            'ESI': 0x2003,
            'ECX': 0
        },
        'expected_read': {
            0x2000: 0x11,
            0x2001: 0x22,
            0x2002: 0x33
        }
    }

@pytest.mark.i686
def case_pause():
    """
    ; PAUSE is REP NOP (F3 90)
    ; It should do nothing visible to regs.
    nop
    pause
    nop
    hlt
    """
    return {
        'expected_regs': {}
    }

@pytest.mark.i686
def case_0f1e_nop_family():
    """
    ; 0F 1E /r should be treated as NOP-family.
    ; Test both ENDBR64 (FA) and ENDBR32 (FB) encodings.
    db 0xF3, 0x0F, 0x1E, 0xFA
    db 0xF3, 0x0F, 0x1E, 0xFB
    mov eax, 0x12345678
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0x12345678
        }
    }

@pytest.mark.i686
def case_scas_repe():
    """
    ; Scan for 0xCC (Not present in 0x11, 0x11, 0x22)
    ; REPE SCAS: Repeat while Equal (ZF=1) AND CX > 0
    ; Compare EAX (Accumulator) with ES:EDI
    mov al, 0x11
    mov edi, 0x2000
    mov ecx, 5
    repe scasb
    hlt
    """
    # 0x2000: 0x11, 0x11, 0x22, 0x33
    # 1. CMP 0x11, [0x2000]=0x11 -> Equal (ZF=1). Continue. ECX=4. EDI=2001.
    # 2. CMP 0x11, [0x2001]=0x11 -> Equal (ZF=1). Continue. ECX=3. EDI=2002.
    # 3. CMP 0x11, [0x2002]=0x22 -> Not Equal (ZF=0). Stop. ECX=2. EDI=2003.
    return {
        'expected_regs': {
            'ECX': 2,
            'EDI': 0x2003
        },
        'expected_read': {
            0x2000: 0x11,
            0x2001: 0x11,
            0x2002: 0x22 # Mismatch here
        },
        # ZF should be 0 (Not Equal). SF=1, CF=1, AF=1, PF=0.
        # Calc: 0x11 - 0x22 = 0xEF. SF=1. 0x1 < 0x2 -> Borrow -> AF=1. 0x11 < 0x22 -> CF=1. PF(EF)=0.
        # Exp: 0x202 | 0x80 | 0x10 | 0x1 = 0x293
        'expected_eflags': 0x293
    }

@pytest.mark.i686
def case_scas_repne():
    """
    ; Scan for 0x55 (Present at end)
    ; REPNE SCAS: Repeat while Not Equal (ZF=0)
    mov al, 0x55
    mov edi, 0x2010
    mov ecx, 10
    repne scasb
    hlt
    """
    # 0x2010..0x2012 = 0x00. 0x2013 = 0x55.
    return {
        'expected_regs': {
            'ECX': 6, # 10 - 4 steps? 0,1,2,3(match) -> 4 steps?
            # 1. [2010]!=55. ECX=9.
            # 2. [2011]!=55. ECX=8.
            # 3. [2012]!=55. ECX=7.
            # 4. [2013]==55. ECX=6. Stop (ZF=1).
            'EDI': 0x2014
        },
        'expected_read': {
            0x2010: 0x00,
            0x2013: 0x55
        },
        # ZF=1 (Match -> 55-55=0). PF=1.
        # Exp: 0x202 | 0x40 | 0x4 = 0x246
        'expected_eflags': 0x246
    }

@pytest.mark.i686
def case_cmps_repe():
    """
    ; Compare two strings
    mov esi, 0x2000 ; 11 11 22 33
    mov edi, 0x2010 ; 11 11 22 44
    mov ecx, 5
    repe cmpsb
    hlt
    """
    # 1. 11==11. Ok.
    # 2. 11==11. Ok.
    # 3. 22==22. Ok.
    # 4. 33!=44. Stop.
    return {
        'expected_regs': {
            'ECX': 1,
            'ESI': 0x2004,
            'EDI': 0x2014
        },
        'expected_read': {
            0x2000: 0x11,
            0x2003: 0x33,
            0x2010: 0x11,
            0x2013: 0x44
        },
        # ZF=0. 33 - 44 -> EF. Same as above (0x293).
        'expected_eflags': 0x293
    }

# =============================================================================
# Stack Operations (PUSHA, POPA, ENTER, LEAVE)
# =============================================================================

@pytest.mark.i686
def case_pusha_popa():
    """
    mov eax, 0x11111111
    mov ecx, 0x22222222
    mov edx, 0x33333333
    mov ebx, 0x44444444
    mov esp, 0x8000
    mov ebp, 0x55555555
    mov esi, 0x66666666
    mov edi, 0x77777777
    
    pusha
    
    ; Clobber regs
    mov eax, 0
    mov ecx, 0
    mov edx, 0
    mov ebx, 0
    mov ebp, 0
    mov esi, 0
    mov edi, 0
    
    popa
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0x11111111,
            'ECX': 0x22222222,
            'EDX': 0x33333333,
            'EBX': 0x44444444,
            'ESP': 0x8000, # Should be restored (actually skipped, but value matches?)
            # POPA ignores ESP value from stack, but ESP should be incremented back to original.
            'EBP': 0x55555555,
            'ESI': 0x66666666,
            'EDI': 0x77777777
        }
    }

@pytest.mark.i686
def case_enter_leave():
    """
    mov ebp, 0x1234
    mov esp, 0x8000
    
    enter 16, 0     ; Push EBP(1234), EBP<-ESP, ESP<-ESP-16
    
    ; Check EBP and ESP
    mov eax, ebp    ; Should be 0x7FFC (since Push EBP decr ESP by 4)
    mov ebx, esp    ; Should be 0x7FEC (7FFC - 16)
    
    leave           ; MOV ESP, EBP; POP EBP
    hlt
    """
    return {
        'expected_regs': {
            'EBP': 0x1234, # Restored
            'ESP': 0x8000, # Restored
            'EAX': 0x7FFC,
            'EBX': 0x7FEC
        }
    }

# =============================================================================
# System / Misc (CPUID, RDTSC, XCHG, LAHF, SAHF)
# =============================================================================

@pytest.mark.i686
def case_cpuid():
    """
    mov eax, 0
    cpuid
    mov esi, ebx
    mov edi, ecx
    mov ebp, edx
    
    mov eax, 1
    cpuid
    hlt
    """
    return {
        'expected_regs': {
            'ESI': 0x756E6547, # "Genu"
            'EBP': 0x49656E69, # "ineI"
            'EDI': 0x6C65746E, # "ntel"
            # Version info check (EAX=1)
            # We don't check exact EAX, but ensure we ran.
            # EAX should be non-zero (Family/Model)
        }
    }

@pytest.mark.i686
def case_rdtsc():
    """
    rdtsc
    mov ecx, eax
    or ecx, edx ; Check if non-zero
    hlt
    """
    # We just expect it to not crash and change EAX/EDX
    return {
        'expected_regs': {
            # Can't predict exact value
        }
    }

@pytest.mark.i686
def case_xchg():
    """
    mov eax, 0xAAAA
    mov ebx, 0xBBBB
    xchg eax, ebx
    
    mov ecx, 0x1234
    mov [0x2000], ecx
    xchg [0x2000], eax
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0x1234,
            'EBX': 0xAAAA,
            'ECX': 0x1234
        },
        'expected_write': {
            0x2000: 0xBBBB
        },
        'expected_read': {
            0x2000: 0x1234 # Initial read before swap
        }
    }

@pytest.mark.i686
def case_lahf_sahf():
    """
    ; Set Flags: SF=1, ZF=0, CF=1
    ; EFLAGS = ...10000101 (roughly)
    mov eax, 0
    push 0x85 ; SF=1, ZF=0, AF=0, PF=1, CF=1 (1000 0101)
    popfd
    
    lahf        ; AH <- Flags
    
    ; Modify Flags
    xor ebx, ebx ; ZF=1
    
    sahf        ; Flags <- AH (Restore)
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0x8600 | 0x2, # AH=86 (1000 0110)? Wait.
            # Pushed: 1000 0101 (85). 
            # LAHF reads: SF(7), ZF(6), 0(5), AF(4), 0(3), PF(2), 1(1), CF(0)
            # If we pushed 85: SF=1, ZF=0, AF=0, PF=1, CF=1.
            # Bit 1 is fixed 1. Bit 3 is 0. Bit 5 is 0.
            # So 1000 0111 = 87?
            # 85 = 1000 0101. Bit 1 is fixed 1.
            # LAHF reads FLAGS. Bit 1 is always 1.
            # So AH should be 1000 0111 = 0x87.
            # EAX start 0. AL=0.
            # Result 0x8700.
            'EAX': 0x8700, 
        },
        'expected_eflags': 0x87 | 0x202 # Restored flags + base flags
    }

# =============================================================================
# Bit / Atomic (BTS, BTC, CMPXCHG)
# =============================================================================

@pytest.mark.i686
def case_bts_btc():
    """
    mov eax, 0
    bts eax, 5      ; Set bit 5. CF was 0 (from mov).
    ; EAX = 0x20. CF=0.
    
    bts eax, 5      ; Set bit 5 again. CF should be 1.
    ; EAX = 0x20. CF=1.
    
    btc eax, 5      ; Complement bit 5.
    ; EAX = 0. CF=1 (value before change was 1)
    
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0
        },
        'expected_eflags': 0x202 | 0x1 # CF=1
    }

@pytest.mark.i686
def case_cmpxchg():
    """
    ; 1. Success case (Dest == EAX)
    mov eax, 0x10
    mov ebx, 0x20
    mov ecx, 0x10   ; Dest
    mov [0x2000], ecx
    
    cmpxchg [0x2000], ebx
    ; [0x2000] should be 0x20. EAX unchanged. ZF=1.
    
    ; 2. Fail case (Dest != EAX)
    mov eax, 0x55   ; Expect 55
    ; [0x2000] is 0x20
    cmpxchg [0x2000], ebx
    ; [0x2000] remains 0x20. EAX becomes 0x20. ZF=0.
    
    hlt
    """
    return {
        'expected_regs': {
            'EAX': 0x20,
            'EBX': 0x20
        },
        'expected_read': {
            0x2000: 0x20
        },
        # 0x55 - 0x20 = 0x35 (0011 0101). Parity Even (4 bits). PF=1.
        # Exp: 0x202 | 0x4 (PF) -> 0x206.
        'expected_eflags': 0x206 & ~0x40 # ZF=0
    }

# =============================================================================
# Runners
# =============================================================================

def test_lods_rep(): run_test(case_lods_rep)
def test_pause(): run_test(case_pause)
def test_0f1e_nop_family(): run_test(case_0f1e_nop_family, check_unicorn=False)
def test_scas_repe(): run_test(case_scas_repe)
def test_scas_repne(): run_test(case_scas_repne)
def test_cmps_repe(): run_test(case_cmps_repe)
# def test_movs_rep(): run_test(case_movs_rep)
def test_pusha_popa(): run_test(case_pusha_popa)
def test_enter_leave(): run_test(case_enter_leave)
def test_cpuid(): run_test(case_cpuid, check_unicorn=False)
def test_rdtsc(): run_test(case_rdtsc, check_unicorn=False)
def test_xchg(): run_test(case_xchg)
def test_lahf_sahf(): run_test(case_lahf_sahf)
def test_bts_btc(): run_test(case_bts_btc)
def test_cmpxchg(): run_test(case_cmpxchg)
