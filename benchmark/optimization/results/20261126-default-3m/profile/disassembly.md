# Addressed disassembly excerpts

Guest addresses are ELF virtual addresses. Host addresses are unslid offsets in the packaged arm64 Mach-O. Branch annotations and opcode bytes are omitted for readability.

### SHA-256 · i386 guest

```asm
3441  movl %eax, %ebx
3443  movl %eax, %esi
3445  movl %eax, 0x4(%esp)
3449  movl (%esp), %ebp
344c  rorl $0x6, %ebx
344f  rorl $0xb, %esi
3452  movl 0x8(%esp), %edi
3456  addl $0x40, 0x60(%esp)
345b  xorl %ebx, %esi
345d  movl %eax, %ebx
345f  movl 0x9c(%esp), %eax
3466  roll $0x7, %ebx
3469  xorl %esi, %ebx
```

### SHA-256 · AArch64 guest

```asm
39d8  ror w7, w3, #0xb
39dc  eor w7, w7, w3, ror #6
39e0  and w6, w6, w3
39e4  ldp w28, w20, [sp, #0xa0]
39e8  eor w6, w6, w5
39ec  eor w7, w7, w3, ror #25

4588  ror w30, w4, #0xb
458c  orr w28, w1, w0
4590  add w25, w25, w7
4594  eor w30, w30, w4, ror #6
```

### SHA-256 · i386 MOV read handler on the host

```asm
7a74c  ldr w8, [x20, #0x40]
7a750  add w8, w28, w8
7a754  add w8, w8, #0x8
7a758  and w9, w8, #0xfff
7a75c  cmp w9, #0xffc
7a760  b.hi 0x7a7a8
7a764  and w9, w8, #0xfffff000
7a768  cmp w9, w23
7a76c  b.ne 0x7a780
7a770  ldr w8, [x24, w8, uxtw]
7a774  str w8, [x20, #0x14]
7a778  ldr x0, [x21, #0x10]!
7a77c  br x0
```

### SHA-256 · AArch64 four-word handler on the host

```asm
3f1c20  ldr x8, [x20, #0x20]
3f1c24  lsl w9, w8, #21
3f1c28  bfxil x9, x8, #11, #21
3f1c2c  ldr w10, [x20, #0x8]
3f1c30  ldr w11, [x20]
3f1c34  orr w10, w11, w10
3f1c38  str x10, [x20, #0xe0]
3f1c3c  ldr w10, [x20, #0xc8]
3f1c40  ldr w11, [x20, #0x38]
3f1c44  add w10, w11, w10
3f1c48  str x10, [x20, #0xc8]
3f1c4c  lsr w10, w8, #6
3f1c50  bfi x10, x8, #26, #6
3f1c54  eor x8, x10, x9
3f1c58  str x8, [x20, #0xf0]
3f1c5c  ldr x0, [x21, #0x40]!
3f1c60  br x0
```

### gzip · i386 guest

```asm
3470  andl $0x7fff, %eax           # imm = 0x7FFF
3475  movzwl (%ebp,%eax,2), %eax
347a  cmpl %eax, (%esp)
347d  jae 0x35b0

348c  leal (%eax,%edi), %edx
348f  movzbl 0x11(%esp), %ebx
3494  cmpb %bl, (%edx,%esi)
3497  jne 0x3470
```

### gzip · AArch64 guest

```asm
3890  and x1, x1, #0x7fff
3894  ldrh w1, [x9, x1, lsl #1]
3898  cmp w8, w1
389c  b.hs 0x39c0

38ac  mov w4, w1
38b0  add x3, x7, x4
38b4  ldrb w5, [x3, x2]
38b8  cmp w5, w10
38bc  b.ne 0x3890
```

### gzip · i386 chain load, limit load, flags and CF branch on the host

```asm
1fa940  add w13, w8, w12
1fa944  mov w8, #0xfff
1fa948  bics wzr, w8, w13
1fa94c  b.eq 0x1faa68
1fa950  and w8, w13, #0xfffff000
1fa954  cmp w8, w23
1fa958  b.ne 0x1faa38
1fa95c  ldrh w27, [x24, w13, uxtw]
1fa960  add w12, w12, w28
1fa964  and w8, w12, #0xfff
1fa968  cmp w8, #0xffc
1fa96c  b.hi 0x1faaf0
1fa970  and w8, w12, #0xfffff000
1fa974  cmp w8, w23
1fa978  b.ne 0x1faaac
1fa97c  ldr w8, [x24, w12, uxtw]
1fa980  mov w10, #0x40
1fa984  subs w11, w8, w27
1fa988  eor w12, w11, w8
1fa98c  csel w10, w10, wzr, eq
1fa990  lsr w13, w11, #31
1fa994  cinc w10, w10, lo
1fa998  orr w10, w10, w13, lsl #7
1fa99c  and w8, w8, w12
1fa9a0  lsr w8, w8, #31
1fa9a4  orr w8, w10, w8, lsl #11
1fa9a8  eor w10, w12, w27
1fa9ac  and w10, w10, #0x10
1fa9b0  mov w12, #-0x8d6
1fa9b4  and w12, w26, w12
1fa9b8  orr w10, w10, w12
1fa9bc  orr w8, w10, w8
1fa9c0  and x10, x26, #0xffffffff00000000
1fa9c4  and x10, x10, #0xffff00ffffffffff
1fa9c8  and w11, w11, #0xff
1fa9cc  orr x10, x10, x11, lsl #40
1fa9d0  orr x26, x10, x8
1fa9d4  tbnz w8, #0x0, 0x1faa18
```

### gzip · AArch64 chain load, limit register, NZCV and carry branch on the host

```asm
4509dc  ldr x8, [x20, #0x8]
4509e0  and x8, x8, #0x7fff
4509e4  str x8, [x20, #0x8]
4509e8  ldr x9, [x20, #0x48]
4509ec  add x8, x9, x8, lsl #1
4509f0  lsr x9, x8, #39
4509f4  cbnz x9, 0x450ab0
4509f8  and x9, x8, #0xfff
4509fc  cmp x9, #0xfff
450a00  b.eq 0x450ab0
450a04  lsr x10, x8, #12
450a08  cmp w10, w24
450a0c  b.ne 0x450a30
450a10  ldrh w8, [x25, x8]
450a14  str x8, [x20, #0x8]
450a18  ldr w9, [x20, #0x40]
450a1c  subs w8, w9, w8
450a20  mrs x26, NZCV
450a24  tbnz w26, #0x1d, 0x450a64
```

### diff · i386 guest

```asm
a5c0  roll $0x7, %edi
a5c3  addl $0x1, %esi
a5c6  addl %edx, %edi
a5c8  movzbl -0x1(%esi), %edx
a5cc  cmpb $0xa, %dl
a5cf  jne 0xa5c0
```

### diff · AArch64 guest

```asm
b1b0  ror x24, x24, #0x39
b1b4  add x24, x24, w21, uxtb
b1b8  ldrb w21, [x28], #0x1
b1bc  cmp w21, #0xa
b1c0  b.ne 0xb1b0
```

### diff · i386 pointer increment and byte-load hit paths on the host

```asm
c2d88  ldr w8, [x20, #0x18]
c2d8c  add w8, w8, #0x1
c2d90  str w8, [x20, #0x18]
c2d94  ldr x0, [x21, #0x10]!
c2d98  br x0

fdc44  ldr w8, [x21, #0x8]
fdc48  ldr w9, [x20, #0x18]
fdc4c  ldr w10, [x20, #0x44]
fdc50  add w8, w9, w8
fdc54  add w8, w8, w10
fdc58  and w9, w8, #0xfffff000
fdc5c  cmp w9, w23
fdc60  b.ne 0xfdc74
fdc64  ldrb w8, [x24, w8, uxtw]
fdc68  str w8, [x20, #0x8]
fdc6c  ldr x0, [x21, #0x10]!
fdc70  br x0
```

### diff · AArch64 post-index byte-load hit path in execution order

```asm
2869c  ldr x13, [x21, #0x8]
286a0  ubfx x10, x13, #22, #5
286a4  add x8, x20, x10, lsl #3
286a8  add x9, x20, #0xf8
286ac  cmp w10, #0x1f
286b0  csel x11, x9, x8, eq
286b4  ldr x14, [x11]
286b8  and x11, x14, #0xf
286bc  ccmp x11, #0x0, #0x4, eq
286c0  b.ne 0x289b0
286c4  ubfx w15, w13, #17, #5
286c8  lsr w11, w13, #30
286cc  cmp w11, #0x1
286d0  ccmp w11, #0x3, #0x4, ne
286d4  cset w12, ne
286d8  cmp w10, #0x1f
286dc  b.eq 0x286f0
286e0  cmp w10, w15
286e4  b.ne 0x286f0

286f0  sbfx x12, x13, #0, #17
286f4  ubfx x27, x13, #32, #3
286f8  add x12, x12, x14
286fc  cmp w11, #0x1
28700  csel x14, x12, x14, ne
28704  cmp w27, #0x4
28708  b.eq 0x28824
2870c  ubfx w16, w13, #27, #3
28710  mov w17, #0x8
28714  lsl w19, w17, w16
28718  and w16, w19, #0xf8
2871c  ubfx w17, w19, #3, #5
28720  cbnz w27, 0x2879c

2879c  lsr x28, x14, #39
287a0  cbnz x28, 0x289b0
287a4  and x28, x14, #0xfff
287a8  mov w0, #0x1000
287ac  sub w17, w0, w17
287b0  cmp x28, x17
287b4  b.hi 0x289b0
287b8  lsr x28, x14, #12
287bc  cmp w28, w24
287c0  b.ne 0x288fc
287c4  add x17, x25, x14
287c8  and w14, w19, #0xff
287cc  cmp w14, #0x20
287d0  b.eq 0x28928
287d4  cmp w14, #0x10
287d8  b.eq 0x287f0
287dc  cmp w14, #0x8
287e0  b.ne 0x2882c
287e4  ldrb w14, [x17]
287e8  tbz x13, #0x23, 0x28930

28930  cmp w15, #0x1f
28934  b.ne 0x28964

28964  cmp w27, #0x3
28968  mov w15, #-0x1
2896c  csinv x15, x15, xzr, eq
28970  and x17, x13, #0x600000000
28974  sub w16, w16, #0x1
28978  mov w19, #0x1
2897c  lsl x16, x19, x16
28980  eor x19, x14, x16
28984  sub x16, x19, x16
28988  mov x19, #0x200000000
2898c  cmp x17, x19
28990  csel x14, x16, x14, eq
28994  and x14, x14, x15
28998  ubfx x13, x13, #17, #5
2899c  str x14, [x20, x13, lsl #3]
289a0  orr w11, w11, #0x2
289a4  cmp w11, #0x3
289a8  b.eq 0x28944

28944  cmp w10, #0x1f
28948  b.ne 0x28958

28958  str x12, [x8]
2895c  ldr x0, [x21, #0x10]!
28960  br x0
```

### ls · i386 guest

```asm
14238  cmpl %ebp, 0x80(%esp)
1423f  setne 0x8(%esp)
14244  cmpl $-0x1, 0x80(%esp)
1424c  jne 0x1425b
1424e  movl 0x10(%esp), %eax
14252  cmpb $0x0, (%eax,%ebp)
14256  setne 0x8(%esp)
1425b  cmpb $0x0, 0x8(%esp)
```

### ls · AArch64 guest

```asm
1383c  cmp w5, #0x2
13840  add x10, x7, x23
13844  cset w20, ne
13848  cmp x2, #0x0
1384c  and w20, w27, w20
13850  cset w21, ne
13854  ands w21, w20, w21
13858  b.ne 0x13d3c
```

### ls · Darwin metadata call from both guests

In the separate single-command JIT disassembly, both guests produced an 832-byte Tier 0 lstat_darwin wrapper. The same call site marshals a path and enters the native lstat stub. The repeated-command sample also caught Tier 1 callers.

```asm
01cc  movz x2, <address part>
01d0  movk x2, <address part>, lsl #16
01d4  movk x2, <address part>, lsl #32
01d8  ldr  x2, [x2]
01dc  blr  x2        // lstat_darwin P/Invoke
```

### grep · i386 DFA loop

```asm
12060  movl %ecx, %ebp
12062  addl $0x1, %ecx
12065  movzbl (%ecx), %eax
12068  movl (%edx,%eax,4), %edi
1206b  cmpl %edi, %ebx
1206d  je 0x12060
```

### grep · AArch64 DFA loop

```asm
11c20  mov x20, x4
11c24  add x4, x4, #0x1
11c28  ldrb w1, [x4]
11c2c  ldr x21, [x5, x1, lsl #3]
11c30  cmp x19, x21
11c34  b.eq 0x11c20
```

### grep · i386 fused compare and branch on the host

```asm
20ddf0  ldr w9, [x20, #0xc]
20ddf4  ldr w10, [x20, #0x1c]
20ddf8  subs w8, w9, w10
20ddfc  b.ne 0x20de58
20de00  mov w8, #0xd120
20de04  ldr x25, [x21, #0x18]
20de08  mov x9, #-0x8d6
20de0c  movk x9, #0xff, lsl #32
20de10  and x9, x26, x9
20de14  orr x26, x9, #0x40
20de18  strb wzr, [x20, x8]
```

### grep · AArch64 separate compare and branch on the host

```asm
1f660  ldr x8, [x21, #0x8]
1f664  ubfx w9, w8, #5, #5
1f668  ubfx w10, w8, #10, #5
1f66c  cmp w9, #0x1f
1f670  b.ne 0x1f69c
1f674  mov x9, #0x0
1f678  cmp w10, #0x1f
1f67c  b.eq 0x1f6a8
1f680  ldr x10, [x20, w10, uxtw #3]
1f684  subs x9, x9, x10
1f688  mrs x26, NZCV
1f68c  and x8, x8, #0x1f
1f690  cmp x8, #0x1f
1f694  b.ne 0x1f6c0
1f698  b 0x1f6c4

32c154  tbnz w26, #0x1e, 0x32c160
32c158  ldr x0, [x21, #0x20]!
32c15c  br x0
32c160  cbz x22, 0x32c188
32c164  ldr x9, [x21, #0x10]
32c168  ldr x8, [x21, #0x30]
32c16c  ldp x0, x10, [x8]
32c170  tbz x10, #0x3f, 0x32c188
32c174  and x11, x10, #0x7fffffffff
32c178  cmp x11, x9
32c17c  ubfx x9, x10, #39, #8
32c180  ccmp x22, x9, #0x0, eq
32c184  b.hs 0x32c19c
32c188  ldr x8, [x20, #0x348]
32c18c  str wzr, [x8, #0x28]
32c190  ldr x9, [x21, #0x10]
32c194  str x9, [x8, #0x30]
32c198  b 0x5414fc
32c19c  sub x22, x22, x9
32c1a0  add x21, x8, #0x20
32c1a4  br x0
```
