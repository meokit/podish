    .text
    .globl _start
_start:
    ldr x9, [sp]
    add x9, sp, x9, lsl #3
    add x9, x9, #16             // envp
1:
    ldr x10, [x9], #8
    cbnz x10, 1b
2:
    ldp x10, x11, [x9], #16     // auxv
    cbz x10, fail
    cmp x10, #9                 // AT_ENTRY
    b.ne 2b
    br x11

fail:
    mov x0, #2
    mov x8, #93
    svc #0
