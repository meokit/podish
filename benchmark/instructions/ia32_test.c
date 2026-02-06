#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

// =================配置区域=================
// 外层循环次数 (C控制)
#define ITERATIONS 10000000 
// 内层汇编展开次数 (汇编控制)
#define REPT_COUNT 100     
// 缓冲区大小
#define BUFFER_SIZE 4096   

// =================辅助工具=================
static inline uint64_t rdtsc() {
    uint32_t lo, hi;
    __asm__ __volatile__ (
        "rdtsc" : "=a" (lo), "=d" (hi)
    );
    return ((uint64_t)hi << 32) | lo;
}

// 打印结果宏
#define PRINT_RESULT(name, start, end) \
    printf("[%-20s] Total Cycles: %llu | Avg Cycles/Inst: %.4f\n", \
           name, (end - start), (double)(end - start) / (ITERATIONS * REPT_COUNT));

// =================测试函数集=================

// 1. NOP 指令速度
void __attribute__((noinline)) test_nop() {
    int count = ITERATIONS;
    
    // %c1 是 Clang/GCC 的一种格式化写法，表示输出常数但不带标点（例如直接输出 100 而不是 $100）
    __asm__ __volatile__ (
        ".align 16 \n\t"
        "1: \n\t"
        ".rept %c1 \n\t"    // 开始重复
        "nop \n\t"          // 待测指令
        ".endr \n\t"        // 结束重复
        
        "dec %0 \n\t"       // 循环计数
        "jnz 1b \n\t"       // 跳转回标签 1
        : "+r" (count)      // Output/Input: 编译器自动分配寄存器存放 count
        : "i" (REPT_COUNT)  // Input: 立即数
        : "cc"              // Clobber: 标志位被修改
    );
}

// 2. 寄存器轮转 (MOV Rotation)
// 测试寄存器之间数据移动的延迟/吞吐
void __attribute__((noinline)) test_mov_rotation() {
    int count = ITERATIONS;
    int a = 1, b = 2, c = 3, d = 4;

    __asm__ __volatile__ (
        ".align 16 \n\t"
        "1: \n\t"
        ".rept %c5 \n\t"
        // 模拟 4 个寄存器轮转: A->B, B->C, C->D, D->A
        // 编译器会把 a,b,c,d 分配到四个通用寄存器中
        "mov %1, %2 \n\t"
        "mov %2, %3 \n\t"
        "mov %3, %4 \n\t"
        "mov %4, %1 \n\t"
        ".endr \n\t"

        "dec %0 \n\t"
        "jnz 1b \n\t"
        : "+r" (count), "+r" (a), "+r" (b), "+r" (c), "+r" (d)
        : "i" (REPT_COUNT)
        : "cc"
    );
}

// 3. XOR 清空 (同一寄存器)
// 测试打破依赖链的速度
void __attribute__((noinline)) test_xor_clear() {
    int count = ITERATIONS;
    int dummy = 0; // 这个变量将被分配到一个寄存器

    __asm__ __volatile__ (
        ".align 16 \n\t"
        "1: \n\t"
        ".rept %c2 \n\t"
        "xor %1, %1 \n\t" // 对同一个寄存器进行 XOR，通常是零延迟
        ".endr \n\t"

        "dec %0 \n\t"
        "jnz 1b \n\t"
        : "+r" (count), "+r" (dummy)
        : "i" (REPT_COUNT)
        : "cc"
    );
}

// 4. 条件跳转 (Conditional Jump)
// 这里测试 "Test + JNZ" 组合，且大部分情况不跳转（Fall through）以测试吞吐
void __attribute__((noinline)) test_cond_jump() {
    int count = ITERATIONS;
    int val = 1; // 非零值，用于 test

    __asm__ __volatile__ (
        ".align 16 \n\t"
        "1: \n\t"
        ".rept %c2 \n\t"
        "test %1, %1 \n\t" // 结果非零，ZF=0
        "jz 2f \n\t"       // 如果为0则跳转（实际上不跳，预测为 Not Taken）
        "nop \n\t"         // 填充指令
        "2: \n\t"          // 本地标签
        ".endr \n\t"

        "dec %0 \n\t"
        "jnz 1b \n\t"
        : "+r" (count)
        : "r" (val), "i" (REPT_COUNT)
        : "cc"
    );
}

// 5. 4K Buffer 连续读取 (修复版)
// 去除了 push/pop，改用 sub 恢复指针，防止 ESP 偏移导致 Crash
void __attribute__((noinline)) test_mem_read(void* buffer) {
    int count = ITERATIONS;
    void* ptr = buffer; 
    int dummy_sink; 

    __asm__ __volatile__ (
        ".align 16 \n\t"
        "1: \n\t"
        
        // --- 核心循环 ---
        ".rept %c3 \n\t"
        "mov (%1), %2 \n\t"    // 读取内存到寄存器 (sink)
        "add $4, %1 \n\t"      // 指针前进 4 字节
        ".endr \n\t"
        // ----------------

        // 修复关键：不用 pop，而是用数学运算把指针拨回去
        // 指针回退总量 = REPT_COUNT * 4
        "sub %4, %1 \n\t"

        "dec %0 \n\t"
        "jnz 1b \n\t"
        : "+r" (count), "+r" (ptr), "=r" (dummy_sink)
        : "i" (REPT_COUNT), "i" (REPT_COUNT * 4) // 传入总偏移量作为立即数 %4
        : "cc", "memory" 
    );
}

// 6. 4K Buffer 连续写入 (修复版)
void __attribute__((noinline)) test_mem_write(void* buffer) {
    int count = ITERATIONS;
    void* ptr = buffer;
    int val = 0xFFFFFFFF; 

    __asm__ __volatile__ (
        ".align 16 \n\t"
        "1: \n\t"
        
        // --- 核心循环 ---
        ".rept %c3 \n\t"
        "mov %2, (%1) \n\t"    // 将寄存器值写入内存
        "add $4, %1 \n\t"      // 指针前进
        ".endr \n\t"
        // ----------------

        // 修复关键：回退指针
        "sub %4, %1 \n\t"

        "dec %0 \n\t"
        "jnz 1b \n\t"
        : "+r" (count), "+r" (ptr)
        : "r" (val), "i" (REPT_COUNT), "i" (REPT_COUNT * 4) // 传入总偏移量 %4
        : "cc", "memory"
    );
}

// =================主函数=================
int main() {
    // 准备一块 4K 对齐的内存，防止跨页带来的额外开销
    void* buffer;
    if (posix_memalign(&buffer, 4096, BUFFER_SIZE) != 0) {
        perror("Memory allocation failed");
        return 1;
    }
    // 预热/填充内存
    memset(buffer, 0xAA, BUFFER_SIZE);

    printf("IA-32 Inline Assembly Benchmark Tool\n");
    printf("Iterations: %d, Unroll Factor: %d\n", ITERATIONS, REPT_COUNT);
    printf("Compiler: Clang / Arch: IA-32\n");
    printf("------------------------------------------------------------\n");

    uint64_t start, end;

    // Test 1: NOP
    start = rdtsc();
    test_nop();
    end = rdtsc();
    PRINT_RESULT("NOP", start, end);

    // Test 2: MOV Rotation
    start = rdtsc();
    test_mov_rotation();
    end = rdtsc();
    PRINT_RESULT("Reg Mov Rotate (4x)", start, end);

    // Test 3: XOR Clear
    start = rdtsc();
    test_xor_clear();
    end = rdtsc();
    PRINT_RESULT("XOR Self Clear", start, end);

    // Test 4: Conditional Jump
    start = rdtsc();
    test_cond_jump();
    end = rdtsc();
    PRINT_RESULT("Cond Jump (NotTaken)", start, end);

    // Test 5: Memory Read
    start = rdtsc();
    test_mem_read(buffer);
    end = rdtsc();
    PRINT_RESULT("4K Mem Read (L1)", start, end);

    // Test 6: Memory Write
    start = rdtsc();
    test_mem_write(buffer);
    end = rdtsc();
    PRINT_RESULT("4K Mem Write (L1)", start, end);

    free(buffer);
    return 0;
}