
artifacts/pin-gnu-20261118/mem2off-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000000c53d4 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op39OpMov_Store_Ebx_EspBaseNoIndexNoSegmentEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj3EEEEExS3_S5_xyyjy>:
   c53d4:      	lsr	x10, x23, #32
   c53d8:      	ldr	w9, [x21, #0x18]
   c53dc:      	ldp	w8, w11, [x20, #0xc]
   c53e0:      	ldr	w12, [x20, #0x40]
   c53e4:      	add	w9, w11, w9
   c53e8:      	add	w9, w9, w12
   c53ec:      	add	w12, w9, #0x3
   c53f0:      	and	w11, w9, #0xfffff000
   c53f4:      	eor	w12, w12, w9
   c53f8:      	and	w12, w12, #0xfffff000
   c53fc:      	eor	w13, w11, w10
   c5400:      	orr	w13, w12, w13
   c5404:      	cbnz	w13, 0xc541c <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op39OpMov_Store_Ebx_EspBaseNoIndexNoSegmentEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj3EEEEExS3_S5_xyyjy+0x48>
   c5408:      	str	w8, [x24, w9, uxtw]
   c540c:      	mov	w8, w23
   c5410:      	orr	x23, x8, x10, lsl #32
   c5414:      	ldr	x0, [x21, #0x20]!
   c5418:      	br	x0
   c541c:      	ubfx	w10, w9, #12, #8
   c5420:      	add	x10, x20, w10, uxtw #4
   c5424:      	ldr	w13, [x10, #0x11a0]
   c5428:      	eor	w13, w13, w11
   c542c:      	orr	w12, w13, w12
   c5430:      	cbnz	w12, 0xc5464 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op39OpMov_Store_Ebx_EspBaseNoIndexNoSegmentEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj3EEEEExS3_S5_xyyjy+0x90>
   c5434:      	mov	w12, #0x11a0            ; =4512
   c5438:      	add	x10, x10, x12
   c543c:      	ldr	x24, [x10, #0x8]
   c5440:      	ldr	w10, [x10, #0x4]
   c5444:      	str	w8, [x24, w9, uxtw]
   c5448:      	tst	w10, #0x1
   c544c:      	csinv	w23, w11, wzr, ne
   c5450:      	mov	x10, x11
   c5454:      	mov	w8, w23
   c5458:      	orr	x23, x8, x10, lsl #32
   c545c:      	ldr	x0, [x21, #0x20]!
   c5460:      	br	x0
   c5464:      	add	x10, x20, #0x7, lsl #12 ; =0x7000
   c5468:      	ldr	w11, [x21, #0x8]
   c546c:      	ldrb	w12, [x21, #0xc]
   c5470:      	sub	w12, w11, w12
   c5474:      	str	w12, [x20, #0x24]
   c5478:      	mov	w12, #0x2               ; =2
   c547c:      	strb	w12, [x10, #0x110]
   c5480:      	str	w9, [x10, #0x130]
   c5484:      	mov	w9, #0x4                ; =4
   c5488:      	str	w9, [x10, #0x134]
   c548c:      	strb	wzr, [x10, #0x13c]
   c5490:      	str	w11, [x10, #0x138]
   c5494:      	str	w8, [x10, #0x120]
   c5498:      	mov	x23, #-0x1              ; =-1
   c549c:      	mov	x24, #0x0               ; =0
   c54a0:      	b	0x14dcb0 <_vsnprintf+0x14dcb0>
