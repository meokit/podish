from tests.runner import Runner

def run_case(func):
    runner = Runner()
    asm = func.__doc__
    runner.run_test(
        name=func.__name__,
        asm=asm,
        **func()
    )

def case_enter_level_0():
    """
    ; Test ENTER 16, 0
    ; Initial state: ESP=0x8000, EBP=0x9000
    mov esp, 0x8000
    mov ebp, 0x9000
    
    ; ENTER alloc_size=16 (0x10), level=0
    enter 0x10, 0
    
    ; Stack Check:
    ; EBP should point to the saved EBP (0x9000).
    mov ebx, [ebp]  ; EBX = *EBP = SavedInitEBP (0x9000)
    
    hlt
    """
    return {
        'expected_regs': {
            'EBP': 0x7FFC,
            'ESP': 0x7FEC,
            'EBX': 0x9000
        }
    }

def case_leave():
    """
    ; Test LEAVE
    ; Setup stack frame manually mimicking ENTER 16, 0
    mov esp, 0x8000
    mov ebp, 0x9000
    
    ; Push old EBP
    push ebp        ; ESP=0x7FFC, *0x7FFC=0x9000
    mov ebp, esp    ; EBP=0x7FFC
    sub esp, 0x10   ; ESP=0x7FEC
    
    ; Execute LEAVE
    leave
    
    ; Verify:
    ; 1. ESP = EBP (0x7FFC)
    ; 2. POP EBP (EBP=0x9000, ESP=0x8000)
    
    hlt
    """
    return {
        'expected_regs': {
            'EBP': 0x9000,
            'ESP': 0x8000
        }
    }

def case_enter_nested():
    """
    ; Test ENTER 8, 1 (Nested Level 1)
    ; Setup previous frame
    mov esp, 0x8000
    mov ebp, 0x9000
    
    ; Level 0 frame (simulated)
    push ebp        ; [0x7FFC] = 0x9000. ESP=0x7FFC
    mov ebp, esp    ; EBP = 0x7FFC
    
    ; Level 1 ENTER
    ; ENTER 8, 1
    enter 8, 1
    
    ; Logic:
    ; 1. Push EBP (0x7FFC) -> [0x7FF8]. ESP=0x7FF8. FrameTemp=0x7FF8.
    ; 2. Level > 0 (1):
    ;    Push FrameTemp (0x7FF8) -> [0x7FF4]. ESP=0x7FF4.
    ; 3. EBP = FrameTemp (0x7FF8).
    ; 4. ESP = ESP - 8 = 0x7FEC.
    
    ; Verification:
    ; EBP should be 0x7FF8.
    ; [EBP] should be Saved Old EBP (0x7FFC).
    ; [EBP-4] (pushed by Step 2) should be FrameTemp (0x7FF8). Wait.
    ; Step 2 pushes what?
    ; "Push FrameTemp" is the last push.
    ; For Level=1, standard Loop (1 to 0) doesn't run.
    ; So we push FrameTemp.
    ; Stack at 0x7FF4 contains 0x7FF8 (FrameTemp).
    ; Stack at 0x7FF8 contains 0x7FFC (Old EBP).
    
    mov ebx, [ebp]     ; EBX = [0x7FF8] = 0x7FFC
    mov ecx, [ebp-4]   ; ECX = [0x7FF4] = 0x7FF8
    
    hlt
    """
    return {
        'expected_regs': {
            'EBP': 0x7FF8,
            'ESP': 0x7FEC, # 0x7FF4 - 8
            'EBX': 0x7FFC,
            'ECX': 0x7FF8
        }
    }

def test_enter_level_0(): run_case(case_enter_level_0)
def test_leave(): run_case(case_leave)
def test_enter_nested(): run_case(case_enter_nested)
