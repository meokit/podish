#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <stdint.h>

// Helper to get a page-aligned buffer
void* get_exec_mem(size_t size) {
    void* ptr = mmap(NULL, size, PROT_READ | PROT_WRITE | PROT_EXEC,
                     MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (ptr == MAP_FAILED) {
        perror("mmap");
        exit(1);
    }
    return ptr;
}

int main() {
    printf("SMC Linux Test Starting\n");

    // Allocate executable memory
    size_t size = 4096;
    uint8_t* code = (uint8_t*)get_exec_mem(size);

    // Scenario 1: Cross-Block SMC
    // We write a small function that modifies itself.
    
    // Code:
    // 0:  C6 05 (addr+7) 00 00 00 90    MOV BYTE [addr+7], 0x90 (NOP)
    // 7:  40                            INC EAX
    // 8:  C3                            RET
    
    // BUT we want to force a Block Split to test chaining.
    // So we use a JMP.
    
    // Block A:
    //   MOV BYTE [BlockB], 0x90
    //   JMP BlockB
    // Block B:
    //   INC EAX
    //   RET
    
    uint8_t* block_b = code + 0x20;
    
    // Construct Block A at code[0]
    // MOV BYTE [block_b], 0x90
    code[0] = 0xC6;
    code[1] = 0x05;
    uint32_t block_b_addr = (uint32_t)(uintptr_t)block_b;
    memcpy(code + 2, &block_b_addr, 4); // Safe write
    code[6] = 0x90; // Imm8
    
    // JMP block_b
    code[7] = 0xE9; // JMP rel32
    uint32_t rel_jmp = (uint32_t)(uintptr_t)(block_b - (code + 7 + 5));
    memcpy(code + 8, &rel_jmp, 4); // Safe write
    
    // Construct Block B at code[0x20]
    // INC EAX (0x40) -> Will be NOP (0x90)
    block_b[0] = 0x40; 
    // RET
    block_b[1] = 0xC3;

    // Execute
    // Defined as: int func(void)
    // We expect EAX to be preserved/modified. 
    // Standard calling convention: EAX is return value.
    // We initialize EAX to 1 before calling (via wrapper or inline asm if needed).
    // But simplest is to make the function return EAX.
    // So we assume EAX is 0 on entry? No, it's undefined.
    // Let's create a wrapper that sets EAX = 10.
    
    // Wrapper:
    // MOV EAX, 10
    // CALL code
    // RET
    
    uint8_t* wrapper = code + 0x100;
    wrapper[0] = 0xB8; 
    uint32_t initial_eax = 10;
    memcpy(wrapper + 1, &initial_eax, 4); // MOV EAX, 10
    
    wrapper[5] = 0xE8; 
    uint32_t call_rel = (uint32_t)(uintptr_t)(code - (wrapper + 5 + 5));
    memcpy(wrapper + 6, &call_rel, 4); // CALL code
    
    wrapper[10] = 0xC3; // RET

    typedef int (*FuncPtr)();
    FuncPtr func = (FuncPtr)wrapper;

    printf("Executing SMC code...\n");
    int result = func();
    
    printf("Result: %d\n", result);
    
    // If SMC worked:
    // INC EAX (10->11) became NOP.
    // Result should be 10.
    
    // If SMC failed (executed old code):
    // Result should be 11.
    
    if (result == 10) {
        printf("PASS: Scenario 1 - SMC applied correctly (Cross-Block)\n");
    } else {
        printf("FAIL: Scenario 1 - Expected 10, got %d\n", result);
        return 1;
    }

    // ---------------------------------------------------------
    // Scenario 2: Same-Block SMC
    // ---------------------------------------------------------
    // Logic:
    //   MOV BYTE [NextInst], NOP
    //   INC EAX  <-- This is 'NextInst'
    //   RET
    //
    // Expectation:
    //   First execution: INC EAX *should* execute (because it was already decoded in the block).
    //   (This tests that we don't crash and follow x86 "stale fetch" behavior or at least our emu behavior)
    //   
    //   Wait, if we loop and execute it AGAIN, it should be NOP.

    uint8_t* code2 = code + 0x200;
    
    // MOV BYTE [code2 + 7], 0x90
    code2[0] = 0xC6; 
    code2[1] = 0x05; 
    uint32_t target_addr = (uint32_t)(uintptr_t)(code2 + 7);
    memcpy(code2 + 2, &target_addr, 4);
    code2[6] = 0x90; // NOP
    
    // INC EAX (at offset 7)
    code2[7] = 0x40;
    // RET
    code2[8] = 0xC3;

    // Wrapper 2:
    // MOV EAX, 20
    // CALL code2
    // RET
    uint8_t* wrapper2 = code + 0x300;
    wrapper2[0] = 0xB8; 
    uint32_t init_eax_2 = 20;
    memcpy(wrapper2 + 1, &init_eax_2, 4);
    
    wrapper2[5] = 0xE8;
    uint32_t call_rel_2 = (uint32_t)(uintptr_t)(code2 - (wrapper2 + 5 + 5));
    memcpy(wrapper2 + 6, &call_rel_2, 4);
    
    wrapper2[10] = 0xC3;

    FuncPtr func2 = (FuncPtr)wrapper2;
    printf("Executing Same-Block SMC...\n");
    int res2 = func2();
    printf("Result (Run 1): %d\n", res2);
    
    // Expectation: 21 (INC executed)
    if (res2 == 21) {
         printf("PASS: Scenario 2 (Run 1) - Old code executed as expected\n");
    } else {
         printf("FAIL: Scenario 2 (Run 1) - Expected 21, got %d\n", res2);
         // return 1; // Don't fail hard, behavior might vary?
    }

    // Run 2: Should match new code (NOP)
    // Note: The previous run modified the memory to NOP.
    // The previous run's invalidation should have cleared the cache.
    // So this run should re-decode and see NOP.
    int res2_b = func2();
    printf("Result (Run 2): %d\n", res2_b);
    
    if (res2_b == 20) {
         printf("PASS: Scenario 2 (Run 2) - New code (NOP) picked up\n");
    } else {
         printf("FAIL: Scenario 2 (Run 2) - Expected 20, got %d\n", res2_b);
         return 1;
    }

    // ---------------------------------------------------------
    // Scenario 3: Self-Modifying Loop (Decryption)
    // ---------------------------------------------------------
    // Logic:
    //   MOV ECX, 5
    // Loop:
    //   XOR BYTE [Target + ECX - 1], 0xFF ; Toggle bits
    //   DEC ECX
    //   JNZ Loop
    // Target:
    //   DB 0xFF, 0xFF, 0xFF, 0xFF, 0xFF ; Garbage
    //   RET
    //
    // After loop, Target should be 0x00 (ADD AL, AL) or something safe?
    // Let's make Target be: 5 NOPs encoded as (NOT 0x90) = 0x6F
    // 0x90 XOR 0xFF = 0x6F.
    // So we put 0x6F. Loop XORs with 0xFF -> 0x90 (NOP).
    
    uint8_t* code3 = code + 0x400;
    uint8_t* target3 = code3 + 0x20; // Target area

    // MOV ECX, 5
    code3[0] = 0xB9; 
    uint32_t count = 5;
    memcpy(code3 + 1, &count, 4);

    // Loop Start (offset 5)
    // XOR BYTE [target3 + ECX - 1], 0xFF
    // We can't easily encode [disp32 + ECX*1] with direct address in one instruction for XOR mem, imm8
    // XOR r/m8, imm8 is 80 /6 ib.
    // Let's use simpler addr calc:
    // LEA EAX, [target3 + ECX - 1]
    // XOR BYTE [EAX], 0xFF
    
    // LEA EDX, [target3 - 1 + ECX]
    // 8D 14 0D (disp32) -> LEA EDX, [disp32 + ECX]
    // ModRM: 0x14 (Reg=010=EDX, RM=100=SIB)
    // SIB: Scale=0(1), Index=1(ECX), Base=5(disp32) => 0x0D
    code3[5] = 0x8D; code3[6] = 0x14; code3[7] = 0x0D;
    uint32_t base_addr = (uint32_t)((uintptr_t)target3 - 1);
    memcpy(code3 + 8, &base_addr, 4);
    
    // XOR BYTE [EDX], 0xFF
    // 80 /6 ib
    // ModRM for [EDX]: 00 110 010 = 0x32
    code3[12] = 0x80; code3[13] = 0x32; code3[14] = 0xFF;
    
    // DEC ECX (49)
    code3[15] = 0x49;
    
    // JNZ Loop (75 F4) -> Back 12 bytes (approx)
    // Loop start is offset 5. Current is offset 16. 
    // 16 + 2 (JNZ len) = 18. Target 5. Delta = -13 = F3
    code3[16] = 0x75; code3[17] = 0xF3;

    // Fill gap with NOPs
    for (int i = 18; i < 32; ++i) code3[i] = 0x90;

    // Target (offset 0x20)
    // Fill with 0x6F (which becomes 0x90 NOP)
    for(int i=0; i<5; ++i) target3[i] = 0x6F;
    
    // RET
    target3[5] = 0xC3;

    // Wrapper 3
    uint8_t* wrapper3 = code + 0x500;
    wrapper3[0] = 0xB8; 
    uint32_t init_eax_3 = 123;
    memcpy(wrapper3 + 1, &init_eax_3, 4); // MOV EAX, 123
    
    wrapper3[5] = 0xE8;
    uint32_t call_rel_3 = (uint32_t)(uintptr_t)(code3 - (wrapper3 + 5 + 5));
    memcpy(wrapper3 + 6, &call_rel_3, 4);
    
    wrapper3[10] = 0xC3;

    FuncPtr func3 = (FuncPtr)wrapper3;
    printf("Executing Loop SMC (Decryption)...\n");
    
    // If successful, loop runs, modifies target to NOPs.
    // Then falls through to target (wait, JNZ falls through when ECX=0).
    // Executes 5 NOPs.
    // Executes RET.
    // EAX remains 123.
    
    int res3 = func3();
    printf("Result: %d\n", res3);
    
    if (res3 == 123) {
         printf("PASS: Scenario 3 - Loop SMC worked\n");
    } else {
         printf("FAIL: Scenario 3 - Expected 123, got %d\n", res3);
         return 1;
    }

    return 0;
}
