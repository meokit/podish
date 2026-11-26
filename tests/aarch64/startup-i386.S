    .text
    .globl _start
_start:
#ifdef EXEC_AARCH64
    movl $11, %eax              // execve
    movl $path, %ebx
    movl $arguments, %ecx
    xorl %edx, %edx
    int $0x80
    movl $3, %ebx
#else
    cmpl $2, (%esp)            // argc
    jne fail
    movl 8(%esp), %eax         // argv[1]
    cmpb $'o', (%eax)
    jne fail
    cmpl $0, 12(%esp)          // argv terminator
    jne fail

    movl $4, %eax              // write(STDOUT_FILENO, ...)
    movl $1, %ebx
    movl $message, %ecx
    movl $message_length, %edx
    int $0x80
    xorl %ebx, %ebx
    jmp exit

fail:
    movl $1, %ebx
exit:
#endif
    movl $1, %eax              // exit
    int $0x80

    .section .rodata
#ifdef EXEC_AARCH64
path:
    .asciz "/dynamic-startup"
arg:
    .asciz "ok"
    .balign 4
arguments:
    .long path, arg, 0
#else
message:
    .ascii "i386 startup ok\n"
    .set message_length, . - message
#endif
