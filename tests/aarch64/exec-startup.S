    .text
    .globl _start
_start:
    adr x0, path
    adr x9, argument
    sub sp, sp, #32
    stp x0, x9, [sp]
    stp xzr, xzr, [sp, #16]
    mov x1, sp                  // argv
    add x2, sp, #24             // envp
    mov x8, #221                // execve
    svc #0
    mov x0, #3
    mov x8, #93
    svc #0

    .section .rodata
path:
#ifdef EXEC_I386
    .asciz "/startup-i386"
#else
    .asciz "/dynamic-startup"
#endif
argument:
    .asciz "ok"
