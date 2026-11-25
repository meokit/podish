.global _start
.section .text
_start:
    mov $2, %eax                    # fork
    int $0x80
    test %eax, %eax
    js fail
    jz child
    mov %eax, %edi

    # The browser test feeds stdin after the child's HTTP request arrives.
    mov $3, %eax                    # read
    xor %ebx, %ebx
    mov $buffer, %ecx
    mov $1, %edx
    int $0x80
    cmp $1, %eax
    jne fail
#if defined(TEST_KILL)
    mov $37, %eax                   # kill
    mov %edi, %ebx
    mov $9, %ecx                    # SIGKILL
    int $0x80
    test %eax, %eax
    jne fail
#endif
    mov $fast_path, %ebx
    call open_file
    mov %eax, %ebx
    mov $3, %eax                    # read
    mov $buffer, %ecx
    mov $4, %edx
    int $0x80
    cmp $4, %eax
    jne fail
    cmpl $0x54534146, buffer        # FAST
    jne fail
    mov $fast_message, %ecx
    mov $fast_length, %edx
    call emit

    mov $7, %eax                    # waitpid
    mov %edi, %ebx
    mov $buffer, %ecx
    xor %edx, %edx
    int $0x80
    cmp %edi, %eax
    jne fail
#if defined(TEST_KILL)
    cmpl $9, buffer
#else
    cmpl $0, buffer
#endif
    jne fail
    jmp done

child:
    mov $slow_path, %ebx
    call open_file
#if defined(TEST_MMAP) || defined(TEST_KILL) || defined(TEST_PUSHA)
    mov %eax, %edi
    mov $192, %eax                  # mmap2
    xor %ebx, %ebx
    mov $4096, %ecx
#if defined(TEST_PUSHA)
    mov $3, %edx                    # PROT_READ | PROT_WRITE
#else
    mov $1, %edx                    # PROT_READ
#endif
    mov $2, %esi                    # MAP_PRIVATE
    xor %ebp, %ebp
    int $0x80
    cmp $-4095, %eax
    jae fail
#if defined(TEST_PUSHA)
    mov %esp, saved_sp
    lea 64(%eax), %esp
    pusha                           # First touch is a write from a multi-store instruction.
    lea 32(%esp), %ecx
    cmp %ecx, 12(%esp)              # PUSHAD saved the original guest ESP.
    jne fail
    cmp %eax, 28(%esp)
    jne fail
    mov saved_sp, %esp
#endif
    cmpl $0x574f4c53, (%eax)        # SLOW
#else
    mov %eax, %ebx
    mov $3, %eax                    # read
    mov $buffer, %ecx
    mov $4, %edx
    int $0x80
    cmp $4, %eax
    jne fail
    cmpl $0x574f4c53, buffer
#endif
    jne fail
    mov $slow_message, %ecx
    mov $slow_length, %edx
    call emit
done:
    xor %ebx, %ebx
    jmp exit
fail:
    mov $1, %ebx
exit:
    mov $1, %eax                    # exit
    int $0x80

open_file:
    mov $5, %eax                    # open
    xor %ecx, %ecx
    xor %edx, %edx
    int $0x80
    test %eax, %eax
    js fail
    ret
emit:
    mov $4, %eax                    # write
    mov $1, %ebx
    int $0x80
    ret

.section .rodata
fast_path: .asciz "/fast"
slow_path: .asciz "/slow"
fast_message: .ascii "FAST completed\n"
.set fast_length, . - fast_message
slow_message: .ascii "SLOW completed\n"
.set slow_length, . - slow_message
.section .bss
buffer: .skip 4
saved_sp: .skip 4
