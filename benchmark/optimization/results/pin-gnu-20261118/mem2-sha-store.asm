
artifacts/pin-gnu-20261118/mem2-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000000c6cb8 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op39OpMov_Store_Ebx_EspBaseNoIndexNoSegmentEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj3EEEEExS3_S5_xyyjyjj>:
   c6cb8:      	lsr	x10, x23, #32
   c6cbc:      	ldr	w8, [x20, #0xc]
   c6cc0:      	ldr	w9, [x21, #0x18]
   c6cc4:      	ldr	w11, [x20, #0x40]
   c6cc8:      	add	w9, w9, w28
   c6ccc:      	add	w9, w9, w11
   c6cd0:      	add	w12, w9, #0x3
   c6cd4:      	and	w11, w9, #0xfffff000
   c6cd8:      	eor	w12, w12, w9
   c6cdc:      	and	w12, w12, #0xfffff000
   c6ce0:      	eor	w13, w11, w10
   c6ce4:      	orr	w13, w12, w13
   c6ce8:      	cbnz	w13, 0xc6d00 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op39OpMov_Store_Ebx_EspBaseNoIndexNoSegmentEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj3EEEEExS3_S5_xyyjyjj+0x48>
   c6cec:      	str	w8, [x24, w9, uxtw]
   c6cf0:      	mov	w8, w23
   c6cf4:      	orr	x23, x8, x10, lsl #32
   c6cf8:      	ldr	x0, [x21, #0x20]!
   c6cfc:      	br	x0
   c6d00:      	ubfx	w10, w9, #12, #8
   c6d04:      	add	x10, x20, w10, uxtw #4
   c6d08:      	ldr	w13, [x10, #0x11a0]
   c6d0c:      	eor	w13, w13, w11
   c6d10:      	orr	w12, w13, w12
   c6d14:      	cbnz	w12, 0xc6d48 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op39OpMov_Store_Ebx_EspBaseNoIndexNoSegmentEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj3EEEEExS3_S5_xyyjyjj+0x90>
   c6d18:      	mov	w12, #0x11a0            ; =4512
   c6d1c:      	add	x10, x10, x12
   c6d20:      	ldr	x24, [x10, #0x8]
   c6d24:      	ldr	w10, [x10, #0x4]
   c6d28:      	str	w8, [x24, w9, uxtw]
   c6d2c:      	tst	w10, #0x1
   c6d30:      	csinv	w23, w11, wzr, ne
   c6d34:      	mov	x10, x11
   c6d38:      	mov	w8, w23
   c6d3c:      	orr	x23, x8, x10, lsl #32
   c6d40:      	ldr	x0, [x21, #0x20]!
   c6d44:      	br	x0
   c6d48:      	add	x10, x20, #0x7, lsl #12 ; =0x7000
   c6d4c:      	ldr	w11, [x21, #0x8]
   c6d50:      	ldrb	w12, [x21, #0xc]
   c6d54:      	sub	w12, w11, w12
   c6d58:      	str	w12, [x20, #0x24]
   c6d5c:      	mov	w12, #0x2               ; =2
   c6d60:      	strb	w12, [x10, #0x110]
   c6d64:      	str	w9, [x10, #0x130]
   c6d68:      	mov	w9, #0x4                ; =4
   c6d6c:      	str	w9, [x10, #0x134]
   c6d70:      	strb	wzr, [x10, #0x13c]
   c6d74:      	str	w11, [x10, #0x138]
   c6d78:      	str	w8, [x10, #0x120]
   c6d7c:      	mov	x23, #-0x1              ; =-1
   c6d80:      	mov	x24, #0x0               ; =0
   c6d84:      	b	0x15127c <_vsnprintf+0x15127c>
