
/Users/jiangyiheng/repos/x86emu/artifacts/pin-gnu-20261118/pin-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000000b0274 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>:
   b0274:      	add	x8, x20, #0x7, lsl #12  ; =0x7000
   b0278:      	lsr	x9, x23, #32
   b027c:      	ldrb	w10, [x21, #0xd]
   b0280:      	cmp	w10, #0xc0
   b0284:      	b.lo	0xb0410 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x19c>
   b0288:      	and	x11, x10, #0x3
   b028c:      	ldr	w11, [x20, x11, lsl #2]
   b0290:      	lsl	w12, w10, #1
   b0294:      	and	w12, w12, #0x8
   b0298:      	lsr	w12, w11, w12
   b029c:      	ldrb	w11, [x21, #0xe]
   b02a0:      	ubfx	w10, w10, #3, #3
   b02a4:      	tbnz	w11, #0x6, 0xb02b4 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x40>
   b02a8:      	and	w11, w12, #0xff
   b02ac:      	str	w11, [x20, w10, uxtw #2]
   b02b0:      	b	0xb02c0 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4c>
   b02b4:      	ubfiz	x10, x10, #2, #32
   b02b8:      	and	w11, w12, #0xff
   b02bc:      	strh	w11, [x20, x10]
   b02c0:      	ldrb	w10, [x21, #0x2d]
   b02c4:      	and	x10, x10, #0x7
   b02c8:      	ldrb	w11, [x21, #0x2e]
   b02cc:      	ldr	w12, [x20, x10, lsl #2]
   b02d0:      	add	w13, w12, #0x1
   b02d4:      	mov	x14, x12
   b02d8:      	bfxil	w14, w13, #0, #16
   b02dc:      	tst	w11, #0x40
   b02e0:      	csinc	w11, w14, w12, ne
   b02e4:      	str	w11, [x20, x10, lsl #2]
   b02e8:      	ldrb	w10, [x21, #0x4d]
   b02ec:      	ubfx	x11, x10, #3, #3
   b02f0:      	and	x10, x10, #0x7
   b02f4:      	ldr	w12, [x20, x10, lsl #2]
   b02f8:      	ldr	w11, [x20, x11, lsl #2]
   b02fc:      	eor	w11, w11, w12
   b0300:      	str	w11, [x20, x10, lsl #2]
   b0304:      	ldrb	w10, [x21, #0x6f]
   b0308:      	tbz	w10, #0x2, 0xb037c <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x108>
   b030c:      	ldr	w11, [x21, #0x70]
   b0310:      	and	w14, w11, #0x1f
   b0314:      	ldrb	w13, [x21, #0x6d]
   b0318:      	and	x12, x13, #0x7
   b031c:      	ldrb	w10, [x21, #0x6e]
   b0320:      	tbnz	w10, #0x6, 0xb0394 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x120>
   b0324:      	cbz	w14, 0xb037c <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x108>
   b0328:      	ldr	w15, [x20, x12, lsl #2]
   b032c:      	and	w16, w11, #0x1f
   b0330:      	sub	w16, w16, #0x1
   b0334:      	lsr	w16, w15, w16
   b0338:      	lsr	w11, w15, w11
   b033c:      	mov	x17, #-0x8d6            ; =-2262
   b0340:      	and	x17, x26, x17
   b0344:      	orr	x19, x17, #0x40
   b0348:      	cmp	w11, #0x0
   b034c:      	csel	x17, x19, x17, eq
   b0350:      	bfxil	x17, x16, #0, #1
   b0354:      	cmp	w15, #0x0
   b0358:      	ccmp	w14, #0x1, #0x0, lt
   b035c:      	orr	x14, x17, #0x800
   b0360:      	csel	x14, x14, x17, eq
   b0364:      	and	x14, x14, #0xffff00ffffffffff
   b0368:      	and	w15, w11, #0xff
   b036c:      	orr	x26, x14, x15, lsl #40
   b0370:      	cmp	w13, #0xbf
   b0374:      	b.ls	0xb046c <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1f8>
   b0378:      	str	w11, [x20, x12, lsl #2]
   b037c:      	mov	w8, w23
   b0380:      	ldr	x0, [x21, #0x80]!
   b0384:      	orr	x23, x8, x9, lsl #32
   b0388:      	ldr	w27, [x20]
   b038c:      	ldr	w28, [x20, #0x10]
   b0390:      	br	x0
   b0394:      	cbz	w14, 0xb037c <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x108>
   b0398:      	ldr	w15, [x20, w12, uxtw #2]
   b039c:      	and	w16, w11, #0x1f
   b03a0:      	and	w17, w15, #0xffff
   b03a4:      	sub	w16, w16, #0x1
   b03a8:      	lsr	w16, w17, w16
   b03ac:      	lsr	w11, w17, w11
   b03b0:      	mov	x17, #-0x8d6            ; =-2262
   b03b4:      	and	x17, x26, x17
   b03b8:      	orr	x19, x17, #0x40
   b03bc:      	cmp	w11, #0x0
   b03c0:      	csel	x17, x19, x17, eq
   b03c4:      	bfxil	x17, x16, #0, #1
   b03c8:      	tst	w15, #0x8000
   b03cc:      	ccmp	w14, #0x1, #0x0, ne
   b03d0:      	orr	x14, x17, #0x800
   b03d4:      	csel	x14, x17, x14, ne
   b03d8:      	and	x14, x14, #0xffff00ffffffffff
   b03dc:      	and	w16, w11, #0xff
   b03e0:      	orr	x26, x14, x16, lsl #40
   b03e4:      	cmp	w13, #0xbf
   b03e8:      	b.ls	0xb04e8 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x274>
   b03ec:      	and	w8, w15, #0xffff0000
   b03f0:      	orr	w8, w11, w8
   b03f4:      	str	w8, [x20, w12, uxtw #2]
   b03f8:      	mov	w8, w23
   b03fc:      	ldr	x0, [x21, #0x80]!
   b0400:      	orr	x23, x8, x9, lsl #32
   b0404:      	ldr	w27, [x20]
   b0408:      	ldr	w28, [x20, #0x10]
   b040c:      	br	x0
   b0410:      	ldp	w12, w11, [x21, #0x14]
   b0414:      	and	x13, x12, #0x3f
   b0418:      	ldr	w13, [x13, x20]
   b041c:      	ubfx	x14, x12, #6, #6
   b0420:      	ldr	w14, [x14, x20]
   b0424:      	ubfx	w15, w12, #12, #2
   b0428:      	lsl	w14, w14, w15
   b042c:      	add	w11, w13, w11
   b0430:      	add	w13, w14, w11
   b0434:      	ldrb	w11, [x21, #0xe]
   b0438:      	sxtb	w14, w11
   b043c:      	and	w15, w13, #0xffff
   b0440:      	cmp	w14, #0x0
   b0444:      	csel	w13, w15, w13, lt
   b0448:      	ubfx	x12, x12, #17, #3
   b044c:      	add	x12, x20, x12, lsl #2
   b0450:      	ldr	w12, [x12, #0x34]
   b0454:      	add	w12, w13, w12
   b0458:      	and	w13, w12, #0xfffff000
   b045c:      	cmp	w13, w23
   b0460:      	b.ne	0xb0564 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x2f0>
   b0464:      	ldrb	w12, [x24, w12, uxtw]
   b0468:      	b	0xb02a0 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x2c>
   b046c:      	ldp	w12, w13, [x21, #0x74]
   b0470:      	ubfx	x14, x12, #6, #6
   b0474:      	ldr	w14, [x14, x20]
   b0478:      	ubfx	w15, w12, #12, #2
   b047c:      	lsl	w14, w14, w15
   b0480:      	and	x15, x12, #0x3f
   b0484:      	ldr	w15, [x15, x20]
   b0488:      	add	w13, w15, w13
   b048c:      	add	w13, w14, w13
   b0490:      	and	w14, w13, #0xffff
   b0494:      	sxtb	w10, w10
   b0498:      	cmp	w10, #0x0
   b049c:      	csel	w10, w14, w13, lt
   b04a0:      	ubfx	x12, x12, #17, #3
   b04a4:      	add	x12, x20, x12, lsl #2
   b04a8:      	ldr	w12, [x12, #0x34]
   b04ac:      	add	w10, w10, w12
   b04b0:      	add	w12, w10, #0x3
   b04b4:      	eor	w13, w12, w10
   b04b8:      	and	w12, w10, #0xfffff000
   b04bc:      	and	w13, w13, #0xfffff000
   b04c0:      	eor	w14, w12, w9
   b04c4:      	orr	w14, w13, w14
   b04c8:      	cbnz	w14, 0xb05a0 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x32c>
   b04cc:      	str	w11, [x24, w10, uxtw]
   b04d0:      	mov	w8, w23
   b04d4:      	ldr	x0, [x21, #0x80]!
   b04d8:      	orr	x23, x8, x9, lsl #32
   b04dc:      	ldr	w27, [x20]
   b04e0:      	ldr	w28, [x20, #0x10]
   b04e4:      	br	x0
   b04e8:      	ldp	w12, w13, [x21, #0x74]
   b04ec:      	ubfx	x14, x12, #6, #6
   b04f0:      	ldr	w14, [x14, x20]
   b04f4:      	ubfx	w15, w12, #12, #2
   b04f8:      	lsl	w14, w14, w15
   b04fc:      	and	x15, x12, #0x3f
   b0500:      	ldr	w15, [x15, x20]
   b0504:      	add	w13, w15, w13
   b0508:      	add	w13, w14, w13
   b050c:      	and	w14, w13, #0xffff
   b0510:      	sxtb	w10, w10
   b0514:      	cmp	w10, #0x0
   b0518:      	csel	w10, w14, w13, lt
   b051c:      	ubfx	x12, x12, #17, #3
   b0520:      	add	x12, x20, x12, lsl #2
   b0524:      	ldr	w12, [x12, #0x34]
   b0528:      	add	w10, w10, w12
   b052c:      	add	w12, w10, #0x1
   b0530:      	eor	w13, w12, w10
   b0534:      	and	w12, w10, #0xfffff000
   b0538:      	and	w13, w13, #0xfffff000
   b053c:      	eor	w14, w12, w9
   b0540:      	orr	w14, w13, w14
   b0544:      	cbnz	w14, 0xb05d0 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x35c>
   b0548:      	strh	w11, [x24, w10, uxtw]
   b054c:      	mov	w8, w23
   b0550:      	ldr	x0, [x21, #0x80]!
   b0554:      	orr	x23, x8, x9, lsl #32
   b0558:      	ldr	w27, [x20]
   b055c:      	ldr	w28, [x20, #0x10]
   b0560:      	br	x0
   b0564:      	ubfx	w9, w12, #12, #8
   b0568:      	add	x9, x20, w9, uxtw #4
   b056c:      	ldr	w14, [x9, #0x1a0]
   b0570:      	cmp	w14, w13
   b0574:      	b.ne	0xb061c <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3a8>
   b0578:      	add	x9, x9, #0x1a0
   b057c:      	ldr	x24, [x9, #0x8]
   b0580:      	ldrb	w12, [x24, w12, uxtw]
   b0584:      	ldr	w9, [x9, #0x4]
   b0588:      	mov	w14, #0x2a              ; =42
   b058c:      	and	w9, w9, w14
   b0590:      	cmp	w9, #0xa
   b0594:      	csinv	w9, w13, wzr, eq
   b0598:      	mov	x23, x13
   b059c:      	b	0xb02a0 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x2c>
   b05a0:      	ubfx	w9, w10, #12, #8
   b05a4:      	add	x9, x20, w9, uxtw #4
   b05a8:      	ldr	w14, [x9, #0x11a0]
   b05ac:      	eor	w14, w14, w12
   b05b0:      	orr	w13, w14, w13
   b05b4:      	cbnz	w13, 0xb0694 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x420>
   b05b8:      	mov	w8, #0x11a0             ; =4512
   b05bc:      	add	x8, x9, x8
   b05c0:      	ldr	x24, [x8, #0x8]
   b05c4:      	ldr	w8, [x8, #0x4]
   b05c8:      	str	w11, [x24, w10, uxtw]
   b05cc:      	b	0xb05fc <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x388>
   b05d0:      	ubfx	w9, w10, #12, #8
   b05d4:      	add	x9, x20, w9, uxtw #4
   b05d8:      	ldr	w14, [x9, #0x11a0]
   b05dc:      	eor	w14, w14, w12
   b05e0:      	orr	w13, w14, w13
   b05e4:      	cbnz	w13, 0xb06dc <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x468>
   b05e8:      	mov	w8, #0x11a0             ; =4512
   b05ec:      	add	x8, x9, x8
   b05f0:      	ldr	x24, [x8, #0x8]
   b05f4:      	ldr	w8, [x8, #0x4]
   b05f8:      	strh	w11, [x24, w10, uxtw]
   b05fc:      	tst	w8, #0x1
   b0600:      	csinv	w23, w12, wzr, ne
   b0604:      	mov	w8, w23
   b0608:      	ldr	x0, [x21, #0x80]!
   b060c:      	orr	x23, x8, x12, lsl #32
   b0610:      	ldr	w27, [x20]
   b0614:      	ldr	w28, [x20, #0x10]
   b0618:      	br	x0
   b061c:      	ldr	w9, [x21, #0x8]
   b0620:      	ldrb	w13, [x21, #0xc]
   b0624:      	sub	w13, w9, w13
   b0628:      	str	w13, [x20, #0x24]
   b062c:      	ldrb	w13, [x8, #0x110]
   b0630:      	cmp	w13, #0x1
   b0634:      	b.ne	0xb0668 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3f4>
   b0638:      	ldrb	w13, [x8, #0x13c]
   b063c:      	cmp	w13, #0x1
   b0640:      	b.ne	0xb0668 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3f4>
   b0644:      	ldr	w13, [x8, #0x138]
   b0648:      	cmp	w13, w9
   b064c:      	b.ne	0xb0668 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3f4>
   b0650:      	mov	x24, #0x0               ; =0
   b0654:      	ldrb	w12, [x8, #0x120]
   b0658:      	strb	wzr, [x8, #0x110]
   b065c:      	mov	w9, #-0x1               ; =-1
   b0660:      	mov	x23, x9
   b0664:      	b	0xb02a0 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x2c>
   b0668:      	mov	w10, #0x1               ; =1
   b066c:      	strb	w10, [x8, #0x110]
   b0670:      	movi.2d	v0, #0000000000000000
   b0674:      	str	q0, [x20, #0x7120]
   b0678:      	str	w12, [x8, #0x130]
   b067c:      	str	w10, [x8, #0x134]
   b0680:      	str	w9, [x8, #0x138]
   b0684:      	strb	wzr, [x8, #0x13c]
   b0688:      	mov	x23, #-0x1              ; =-1
   b068c:      	mov	x24, #0x0               ; =0
   b0690:      	b	0x144268 <_vsnprintf+0x144268>
   b0694:      	ldr	w9, [x21, #0x68]
   b0698:      	ldrb	w12, [x21, #0x6c]
   b069c:      	sub	w12, w9, w12
   b06a0:      	str	w12, [x20, #0x24]
   b06a4:      	mov	w12, #0x2               ; =2
   b06a8:      	strb	w12, [x8, #0x110]
   b06ac:      	str	w10, [x8, #0x130]
   b06b0:      	mov	w10, #0x4               ; =4
   b06b4:      	str	w10, [x8, #0x134]
   b06b8:      	strb	wzr, [x8, #0x13c]
   b06bc:      	str	w9, [x8, #0x138]
   b06c0:      	str	w11, [x8, #0x120]
   b06c4:      	ldr	w27, [x20]
   b06c8:      	ldr	w28, [x20, #0x10]
   b06cc:      	add	x21, x21, #0x60
   b06d0:      	mov	x23, #-0x1              ; =-1
   b06d4:      	mov	x24, #0x0               ; =0
   b06d8:      	b	0x14425c <_vsnprintf+0x14425c>
   b06dc:      	ldr	w9, [x21, #0x68]
   b06e0:      	ldrb	w12, [x21, #0x6c]
   b06e4:      	sub	w12, w9, w12
   b06e8:      	str	w12, [x20, #0x24]
   b06ec:      	mov	w12, #0x2               ; =2
   b06f0:      	strb	w12, [x8, #0x110]
   b06f4:      	str	w10, [x8, #0x130]
   b06f8:      	str	w12, [x8, #0x134]
   b06fc:      	strb	wzr, [x8, #0x13c]
   b0700:      	str	w9, [x8, #0x138]
   b0704:      	strh	w11, [x8, #0x120]
   b0708:      	ldr	w27, [x20]
   b070c:      	ldr	w28, [x20, #0x10]
   b0710:      	add	x21, x21, #0x60
   b0714:      	mov	x23, #-0x1              ; =-1
   b0718:      	mov	x24, #0x0               ; =0
   b071c:      	b	0x14425c <_vsnprintf+0x14425c>
