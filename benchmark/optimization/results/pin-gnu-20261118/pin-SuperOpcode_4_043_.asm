
/Users/jiangyiheng/repos/x86emu/artifacts/pin-gnu-20261118/pin-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000099138 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>:
   99138:      	sub	sp, sp, #0x20
   9913c:      	stp	x29, x30, [sp, #0x10]
   99140:      	add	x9, x20, #0x7, lsl #12  ; =0x7000
   99144:      	ldp	w8, w10, [x21, #0x14]
   99148:      	and	x11, x8, #0x3f
   9914c:      	ldr	w11, [x11, x20]
   99150:      	ubfx	x12, x8, #6, #6
   99154:      	ldr	w12, [x12, x20]
   99158:      	ubfx	w13, w8, #12, #2
   9915c:      	lsl	w12, w12, w13
   99160:      	add	w10, w11, w10
   99164:      	add	w10, w10, w12
   99168:      	ldrsb	w11, [x21, #0xe]
   9916c:      	and	w12, w10, #0xffff
   99170:      	cmp	w11, #0x0
   99174:      	csel	w10, w12, w10, lt
   99178:      	ubfx	x8, x8, #17, #3
   9917c:      	add	x19, x20, #0x34
   99180:      	ldr	w8, [x19, x8, lsl #2]
   99184:      	add	w10, w10, w8
   99188:      	add	w8, w10, #0x3
   9918c:      	eor	w8, w8, w10
   99190:      	and	w11, w10, #0xfffff000
   99194:      	and	w8, w8, #0xfffff000
   99198:      	eor	w12, w11, w23
   9919c:      	orr	w12, w8, w12
   991a0:      	cbnz	w12, 0x99308 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1d0>
   991a4:      	lsr	x8, x23, #32
   991a8:      	ldr	w10, [x24, w10, uxtw]
   991ac:      	str	w10, [x20, #0x8]
   991b0:      	ldp	w10, w11, [x21, #0x34]
   991b4:      	ubfx	x12, x10, #6, #6
   991b8:      	ldr	w12, [x12, x20]
   991bc:      	ubfx	w13, w10, #12, #2
   991c0:      	lsl	w12, w12, w13
   991c4:      	and	x13, x10, #0x3f
   991c8:      	ldr	w13, [x13, x20]
   991cc:      	add	w11, w13, w11
   991d0:      	add	w11, w11, w12
   991d4:      	ldrsb	w12, [x21, #0x2e]
   991d8:      	and	w13, w11, #0xffff
   991dc:      	cmp	w12, #0x0
   991e0:      	csel	w11, w13, w11, lt
   991e4:      	ubfx	x10, x10, #17, #3
   991e8:      	ldr	w10, [x19, x10, lsl #2]
   991ec:      	add	w10, w11, w10
   991f0:      	add	w11, w10, #0x3
   991f4:      	eor	w12, w11, w10
   991f8:      	and	w11, w10, #0xfffff000
   991fc:      	and	w12, w12, #0xfffff000
   99200:      	eor	w13, w11, w23
   99204:      	orr	w13, w12, w13
   99208:      	cbnz	w13, 0x99348 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x210>
   9920c:      	ldr	w10, [x24, w10, uxtw]
   99210:      	mov	w9, #0x6e40             ; =28224
   99214:      	add	x27, x20, x9
   99218:      	str	w10, [x20]
   9921c:      	ldrb	w9, [x21, #0x4d]
   99220:      	cmp	w9, #0xc0
   99224:      	b.lo	0x99388 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x250>
   99228:      	and	x10, x9, #0x7
   9922c:      	add	x10, x20, x10, lsl #2
   99230:      	ldr	w10, [x10]
   99234:      	adds	w11, w10, #0x1
   99238:      	mov	x12, #-0x8d6            ; =-2262
   9923c:      	and	x12, x26, x12
   99240:      	orr	x13, x12, #0x80
   99244:      	cmp	w11, #0x0
   99248:      	csel	x11, x13, x12, lt
   9924c:      	adds	w2, w10, #0x1
   99250:      	orr	x12, x12, #0x40
   99254:      	csel	x11, x12, x11, hs
   99258:      	eor	w12, w2, w10
   9925c:      	cmp	w12, #0x0
   99260:      	ccmn	w10, #0x1, #0x4, lt
   99264:      	mov	w10, #0x800             ; =2048
   99268:      	csel	x10, x10, xzr, gt
   9926c:      	and	w12, w12, #0x10
   99270:      	and	x13, x11, #0xffffffff00000000
   99274:      	and	x13, x13, #0xffff00ffffffffff
   99278:      	and	w14, w2, #0xff
   9927c:      	orr	x13, x13, x14, lsl #40
   99280:      	and	x11, x11, #0xfffffffe
   99284:      	bfxil	x12, x26, #0, #1
   99288:      	orr	x10, x10, x11
   9928c:      	orr	x10, x12, x10
   99290:      	orr	x26, x10, x13
   99294:      	cmp	w9, #0xc0
   99298:      	b.lo	0x993ec <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x2b4>
   9929c:      	and	w9, w9, #0x7
   992a0:      	str	w2, [x20, w9, uxtw #2]
   992a4:      	ldrb	w9, [x21, #0x6d]
   992a8:      	cmp	w9, #0xc0
   992ac:      	b.lo	0x99450 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x318>
   992b0:      	and	x10, x9, #0x3
   992b4:      	ldr	w10, [x20, x10, lsl #2]
   992b8:      	lsl	w11, w9, #1
   992bc:      	and	w11, w11, #0x8
   992c0:      	lsr	w11, w10, w11
   992c4:      	ldrb	w10, [x21, #0x6e]
   992c8:      	ubfx	w9, w9, #3, #3
   992cc:      	tbnz	w10, #0x6, 0x992dc <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1a4>
   992d0:      	and	w10, w11, #0xff
   992d4:      	str	w10, [x20, w9, uxtw #2]
   992d8:      	b	0x992e8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1b0>
   992dc:      	ubfiz	x9, x9, #2, #32
   992e0:      	and	w10, w11, #0xff
   992e4:      	strh	w10, [x20, x9]
   992e8:      	mov	w9, w23
   992ec:      	ldr	x0, [x21, #0x80]!
   992f0:      	orr	x23, x9, x8, lsl #32
   992f4:      	ldr	w27, [x20]
   992f8:      	ldr	w28, [x20, #0x10]
   992fc:      	ldp	x29, x30, [sp, #0x10]
   99300:      	add	sp, sp, #0x20
   99304:      	br	x0
   99308:      	ubfx	w12, w10, #12, #8
   9930c:      	add	x12, x20, w12, uxtw #4
   99310:      	ldr	w13, [x12, #0x1a0]
   99314:      	eor	w13, w13, w11
   99318:      	orr	w8, w13, w8
   9931c:      	cbnz	w8, 0x994a8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x370>
   99320:      	add	x8, x12, #0x1a0
   99324:      	ldr	x24, [x8, #0x8]
   99328:      	ldr	w10, [x24, w10, uxtw]
   9932c:      	ldr	w8, [x8, #0x4]
   99330:      	mov	w12, #0x2a              ; =42
   99334:      	and	w8, w8, w12
   99338:      	cmp	w8, #0xa
   9933c:      	csinv	w8, w11, wzr, eq
   99340:      	mov	x23, x11
   99344:      	b	0x991ac <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x74>
   99348:      	ubfx	w8, w10, #12, #8
   9934c:      	add	x8, x20, w8, uxtw #4
   99350:      	ldr	w13, [x8, #0x1a0]
   99354:      	eor	w13, w13, w11
   99358:      	orr	w12, w13, w12
   9935c:      	cbnz	w12, 0x994f4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3bc>
   99360:      	add	x8, x8, #0x1a0
   99364:      	ldr	x24, [x8, #0x8]
   99368:      	ldr	w10, [x24, w10, uxtw]
   9936c:      	ldr	w8, [x8, #0x4]
   99370:      	mov	w9, #0x2a               ; =42
   99374:      	and	w8, w8, w9
   99378:      	cmp	w8, #0xa
   9937c:      	csinv	w8, w11, wzr, eq
   99380:      	mov	x23, x11
   99384:      	b	0x99210 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xd8>
   99388:      	ldp	w10, w11, [x21, #0x54]
   9938c:      	ubfx	x12, x10, #6, #6
   99390:      	ldr	w12, [x12, x20]
   99394:      	ubfx	w13, w10, #12, #2
   99398:      	lsl	w12, w12, w13
   9939c:      	and	x13, x10, #0x3f
   993a0:      	ldr	w13, [x13, x20]
   993a4:      	add	w11, w13, w11
   993a8:      	add	w11, w12, w11
   993ac:      	ldrsb	w12, [x21, #0x4e]
   993b0:      	and	w13, w11, #0xffff
   993b4:      	cmp	w12, #0x0
   993b8:      	csel	w11, w13, w11, lt
   993bc:      	ubfx	x10, x10, #17, #3
   993c0:      	ldr	w10, [x19, x10, lsl #2]
   993c4:      	add	w1, w11, w10
   993c8:      	add	w10, w1, #0x3
   993cc:      	eor	w10, w10, w1
   993d0:      	and	w11, w1, #0xfffff000
   993d4:      	and	w10, w10, #0xfffff000
   993d8:      	eor	w12, w11, w23
   993dc:      	orr	w12, w10, w12
   993e0:      	cbnz	w12, 0x99540 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x408>
   993e4:      	add	x10, x24, x1
   993e8:      	b	0x99230 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xf8>
   993ec:      	ldp	w9, w10, [x21, #0x54]
   993f0:      	ubfx	x11, x9, #6, #6
   993f4:      	ldr	w11, [x11, x20]
   993f8:      	ubfx	w12, w9, #12, #2
   993fc:      	lsl	w11, w11, w12
   99400:      	and	x12, x9, #0x3f
   99404:      	ldr	w12, [x12, x20]
   99408:      	add	w10, w12, w10
   9940c:      	add	w10, w11, w10
   99410:      	ldrsb	w11, [x21, #0x4e]
   99414:      	and	w12, w10, #0xffff
   99418:      	cmp	w11, #0x0
   9941c:      	csel	w10, w12, w10, lt
   99420:      	ubfx	x9, x9, #17, #3
   99424:      	ldr	w9, [x19, x9, lsl #2]
   99428:      	add	w1, w10, w9
   9942c:      	add	w9, w1, #0x3
   99430:      	eor	w10, w9, w1
   99434:      	and	w9, w1, #0xfffff000
   99438:      	and	w10, w10, #0xfffff000
   9943c:      	eor	w11, w9, w8
   99440:      	orr	w11, w10, w11
   99444:      	cbnz	w11, 0x99580 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x448>
   99448:      	str	w2, [x24, x1]
   9944c:      	b	0x992a4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x16c>
   99450:      	ldp	w11, w10, [x21, #0x74]
   99454:      	and	x12, x11, #0x3f
   99458:      	ldr	w12, [x12, x20]
   9945c:      	ubfx	x13, x11, #6, #6
   99460:      	ldr	w13, [x13, x20]
   99464:      	ubfx	w14, w11, #12, #2
   99468:      	lsl	w13, w13, w14
   9946c:      	add	w10, w12, w10
   99470:      	add	w12, w13, w10
   99474:      	ldrb	w10, [x21, #0x6e]
   99478:      	sxtb	w13, w10
   9947c:      	and	w14, w12, #0xffff
   99480:      	cmp	w13, #0x0
   99484:      	csel	w12, w14, w12, lt
   99488:      	ubfx	x11, x11, #17, #3
   9948c:      	ldr	w11, [x19, x11, lsl #2]
   99490:      	add	w11, w12, w11
   99494:      	and	w12, w11, #0xfffff000
   99498:      	cmp	w23, w12
   9949c:      	b.ne	0x995bc <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x484>
   994a0:      	ldrb	w11, [x24, w11, uxtw]
   994a4:      	b	0x992c8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x190>
   994a8:      	ldr	w8, [x21, #0x8]
   994ac:      	ldrb	w11, [x21, #0xc]
   994b0:      	sub	w11, w8, w11
   994b4:      	str	w11, [x20, #0x24]
   994b8:      	ldrb	w11, [x9, #0x110]
   994bc:      	cmp	w11, #0x1
   994c0:      	b.ne	0x995f8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4c0>
   994c4:      	ldrb	w11, [x9, #0x13c]
   994c8:      	cmp	w11, #0x1
   994cc:      	b.ne	0x995f8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4c0>
   994d0:      	ldr	w11, [x9, #0x138]
   994d4:      	cmp	w11, w8
   994d8:      	b.ne	0x995f8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4c0>
   994dc:      	mov	x24, #0x0               ; =0
   994e0:      	ldr	w10, [x9, #0x120]
   994e4:      	strb	wzr, [x9, #0x110]
   994e8:      	mov	w8, #-0x1               ; =-1
   994ec:      	mov	x23, x8
   994f0:      	b	0x991ac <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x74>
   994f4:      	ldr	w8, [x21, #0x28]
   994f8:      	ldrb	w11, [x21, #0x2c]
   994fc:      	sub	w11, w8, w11
   99500:      	str	w11, [x20, #0x24]
   99504:      	ldrb	w11, [x9, #0x110]
   99508:      	cmp	w11, #0x1
   9950c:      	b.ne	0x99630 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4f8>
   99510:      	ldrb	w11, [x9, #0x13c]
   99514:      	cmp	w11, #0x1
   99518:      	b.ne	0x99630 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4f8>
   9951c:      	ldr	w11, [x9, #0x138]
   99520:      	cmp	w11, w8
   99524:      	b.ne	0x99630 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4f8>
   99528:      	mov	x24, #0x0               ; =0
   9952c:      	ldr	w10, [x9, #0x120]
   99530:      	strb	wzr, [x9, #0x110]
   99534:      	mov	w8, #-0x1               ; =-1
   99538:      	mov	x23, x8
   9953c:      	b	0x99210 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xd8>
   99540:      	ubfx	w8, w1, #12, #8
   99544:      	add	x8, x20, w8, uxtw #4
   99548:      	ldr	w12, [x8, #0x1a0]
   9954c:      	eor	w12, w12, w11
   99550:      	orr	w10, w12, w10
   99554:      	cbnz	w10, 0x9966c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x534>
   99558:      	add	x8, x8, #0x1a0
   9955c:      	ldr	w12, [x8, #0x4]
   99560:      	ldr	x24, [x8, #0x8]
   99564:      	add	x10, x24, x1
   99568:      	mov	w8, #0x2a               ; =42
   9956c:      	and	w8, w12, w8
   99570:      	cmp	w8, #0xa
   99574:      	csinv	w8, w11, wzr, eq
   99578:      	mov	x23, x11
   9957c:      	b	0x99230 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xf8>
   99580:      	ubfx	w12, w1, #12, #8
   99584:      	add	x11, x20, w12, uxtw #4
   99588:      	mov	w8, #0x11a0             ; =4512
   9958c:      	add	x8, x11, x8
   99590:      	ldr	w11, [x11, #0x11a0]
   99594:      	eor	w13, w11, w9
   99598:      	orr	w10, w13, w10
   9959c:      	cbnz	w10, 0x996b4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x57c>
   995a0:      	ldr	x24, [x8, #0x8]
   995a4:      	ldr	w8, [x8, #0x4]
   995a8:      	str	w2, [x24, x1]
   995ac:      	tst	w8, #0x1
   995b0:      	csinv	w23, w9, wzr, ne
   995b4:      	mov	x8, x9
   995b8:      	b	0x992a4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x16c>
   995bc:      	ubfx	w8, w11, #12, #8
   995c0:      	add	x8, x20, w8, uxtw #4
   995c4:      	ldr	w13, [x8, #0x1a0]
   995c8:      	cmp	w13, w12
   995cc:      	b.ne	0x99720 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x5e8>
   995d0:      	add	x8, x8, #0x1a0
   995d4:      	ldr	x24, [x8, #0x8]
   995d8:      	ldrb	w11, [x24, w11, uxtw]
   995dc:      	ldr	w8, [x8, #0x4]
   995e0:      	mov	w13, #0x2a              ; =42
   995e4:      	and	w8, w8, w13
   995e8:      	cmp	w8, #0xa
   995ec:      	csinv	w8, w12, wzr, eq
   995f0:      	mov	x23, x12
   995f4:      	b	0x992c8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x190>
   995f8:      	mov	w11, #0x1               ; =1
   995fc:      	strb	w11, [x9, #0x110]
   99600:      	movi.2d	v0, #0000000000000000
   99604:      	str	q0, [x20, #0x7120]
   99608:      	str	w10, [x9, #0x130]
   9960c:      	mov	w10, #0x4               ; =4
   99610:      	str	w10, [x9, #0x134]
   99614:      	str	w8, [x9, #0x138]
   99618:      	strb	wzr, [x9, #0x13c]
   9961c:      	mov	x23, #-0x1              ; =-1
   99620:      	mov	x24, #0x0               ; =0
   99624:      	ldp	x29, x30, [sp, #0x10]
   99628:      	add	sp, sp, #0x20
   9962c:      	b	0x144268 <_vsnprintf+0x144268>
   99630:      	mov	w11, #0x1               ; =1
   99634:      	strb	w11, [x9, #0x110]
   99638:      	movi.2d	v0, #0000000000000000
   9963c:      	str	q0, [x20, #0x7120]
   99640:      	str	w10, [x9, #0x130]
   99644:      	mov	w10, #0x4               ; =4
   99648:      	str	w10, [x9, #0x134]
   9964c:      	str	w8, [x9, #0x138]
   99650:      	strb	wzr, [x9, #0x13c]
   99654:      	add	x21, x21, #0x20
   99658:      	mov	x23, #-0x1              ; =-1
   9965c:      	mov	x24, #0x0               ; =0
   99660:      	ldp	x29, x30, [sp, #0x10]
   99664:      	add	sp, sp, #0x20
   99668:      	b	0x144268 <_vsnprintf+0x144268>
   9966c:      	mov	w8, #0xe                ; =14
   99670:      	strb	w8, [sp, #0x4]
   99674:      	strb	wzr, [sp, #0x8]
   99678:      	ldr	w8, [x21, #0x48]
   9967c:      	ldrb	w9, [x21, #0x4c]
   99680:      	sub	w8, w8, w9
   99684:      	str	w8, [x20, #0x24]
   99688:      	add	x8, sp, #0x4
   9968c:      	add	x0, x20, #0x180
   99690:      	bl	0x83060 <__ZN8fiberish3mem3Mmu9read_slowIjEENSt3__18expectedIT_NS0_9FaultCodeEEEj>
   99694:      	ldrb	w8, [sp, #0x8]
   99698:      	tbz	w8, #0x0, 0x9976c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x634>
   9969c:      	mov	x24, #0x0               ; =0
   996a0:      	ldrb	w9, [x21, #0x4d]
   996a4:      	mov	w23, #-0x1              ; =-1
   996a8:      	add	x10, sp, #0x4
   996ac:      	mov	x8, x23
   996b0:      	b	0x99230 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xf8>
   996b4:      	add	x10, x20, x12, lsl #4
   996b8:      	ldr	w12, [x10, #0x1a0]
   996bc:      	cmp	w12, w9
   996c0:      	b.eq	0x997d8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x6a0>
   996c4:      	cmp	w11, w9
   996c8:      	b.eq	0x997ec <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x6b4>
   996cc:      	ldr	w8, [x10, #0x21a0]
   996d0:      	cmp	w8, w9
   996d4:      	b.ne	0x996e4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x5ac>
   996d8:      	mov	w8, #0x21a0             ; =8608
   996dc:      	mov	w9, #-0x1               ; =-1
   996e0:      	str	w9, [x10, x8]
   996e4:      	mov	w8, #0xe                ; =14
   996e8:      	strh	w8, [sp, #0xe]
   996ec:      	ldr	w8, [x21, #0x48]
   996f0:      	ldrb	w9, [x21, #0x4c]
   996f4:      	sub	w8, w8, w9
   996f8:      	str	w8, [x20, #0x24]
   996fc:      	add	x8, sp, #0xe
   99700:      	add	x0, x20, #0x180
   99704:      	bl	0x822ac <__ZN8fiberish3mem3Mmu10write_slowIjEENSt3__18expectedIvNS0_9FaultCodeEEEjT_>
   99708:      	ldrb	w8, [sp, #0xf]
   9970c:      	tbz	w8, #0x0, 0x9976c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x634>
   99710:      	mov	x24, #0x0               ; =0
   99714:      	mov	w23, #-0x1              ; =-1
   99718:      	mov	x8, x23
   9971c:      	b	0x992a4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x16c>
   99720:      	ldr	w8, [x21, #0x68]
   99724:      	ldrb	w12, [x21, #0x6c]
   99728:      	sub	w12, w8, w12
   9972c:      	str	w12, [x20, #0x24]
   99730:      	ldrb	w12, [x27, #0x2d0]
   99734:      	cmp	w12, #0x1
   99738:      	b.ne	0x99798 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x660>
   9973c:      	ldrb	w12, [x27, #0x2fc]
   99740:      	cmp	w12, #0x1
   99744:      	b.ne	0x99798 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x660>
   99748:      	ldr	w12, [x27, #0x2f8]
   9974c:      	cmp	w12, w8
   99750:      	b.ne	0x99798 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x660>
   99754:      	mov	x24, #0x0               ; =0
   99758:      	ldrb	w11, [x27, #0x2e0]
   9975c:      	strb	wzr, [x27, #0x2d0]
   99760:      	mov	w8, #-0x1               ; =-1
   99764:      	mov	x23, x8
   99768:      	b	0x992c8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x190>
   9976c:      	str	x26, [x20, #0x28]
   99770:      	ldrb	w8, [x27]
   99774:      	tbnz	w8, #0x0, 0x99788 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x650>
   99778:      	ldr	w8, [x21, #0x48]
   9977c:      	ldrb	w9, [x21, #0x4c]
   99780:      	sub	w8, w8, w9
   99784:      	str	w8, [x20, #0x24]
   99788:      	mov	x0, x22
   9978c:      	ldp	x29, x30, [sp, #0x10]
   99790:      	add	sp, sp, #0x20
   99794:      	ret
   99798:      	mov	w9, #0x1                ; =1
   9979c:      	strb	w9, [x27, #0x2d0]
   997a0:      	movi.2d	v0, #0000000000000000
   997a4:      	str	q0, [x20, #0x7120]
   997a8:      	str	w11, [x27, #0x2f0]
   997ac:      	str	w9, [x27, #0x2f4]
   997b0:      	str	w8, [x27, #0x2f8]
   997b4:      	strb	wzr, [x27, #0x2fc]
   997b8:      	ldr	w27, [x20]
   997bc:      	ldr	w28, [x20, #0x10]
   997c0:      	add	x21, x21, #0x60
   997c4:      	mov	x23, #-0x1              ; =-1
   997c8:      	mov	x24, #0x0               ; =0
   997cc:      	ldp	x29, x30, [sp, #0x10]
   997d0:      	add	sp, sp, #0x20
   997d4:      	b	0x144268 <_vsnprintf+0x144268>
   997d8:      	add	x12, x10, #0x1a0
   997dc:      	mov	w13, #-0x1              ; =-1
   997e0:      	str	w13, [x12]
   997e4:      	cmp	w11, w9
   997e8:      	b.ne	0x996cc <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x594>
   997ec:      	mov	w11, #-0x1              ; =-1
   997f0:      	str	w11, [x8]
   997f4:      	ldr	w8, [x10, #0x21a0]
   997f8:      	cmp	w8, w9
   997fc:      	b.eq	0x996d8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x5a0>
   99800:      	b	0x996e4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x5ac>
