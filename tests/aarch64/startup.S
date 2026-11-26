    .text
    .globl _start
_start:
    ldr x9, [sp]                // argc
    cmp x9, #2
    b.ne fail
    ldr x9, [sp, #16]           // argv[1]
    ldrb w10, [x9]
    cmp w10, #'o'
    b.ne fail
    ldr x9, [sp, #24]           // argv terminator
    cbnz x9, fail
    adr x9, zero_data
    ldr x10, [x9]
    cbnz x10, fail

    mov x0, #1                  // write(STDOUT_FILENO, ...)
    adr x1, message
    mov x2, #message_length
    mov x8, #64
    svc #0
    mov x0, #0
    b exit

fail:
    mov x0, #1
exit:
    mov x8, #93
    svc #0

    .section .rodata
message:
    .ascii "AArch64 startup ok\n"
    .set message_length, . - message

    .bss
    .balign 8
zero_data:
    .skip 8
