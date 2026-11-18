
/Users/jiangyiheng/repos/x86emu/artifacts/pin-gnu-20261118/off-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000096414 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy>:
   96414:      	add	x8, x20, #0x7, lsl #12  ; =0x7000
   96418:      	lsr	x9, x23, #32
   9641c:      	ldr	w10, [x20, #0x18]
   96420:      	ldrb	w11, [x21, #0xd]
   96424:      	and	x11, x11, #0x7
   96428:      	str	w10, [x20, x11, lsl #2]
   9642c:      	ldrb	w10, [x21, #0x2d]
   96430:      	cmp	w10, #0xc0
   96434:      	b.lo	0x96544 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x130>
   96438:      	and	x11, x10, #0x3
   9643c:      	ldr	w11, [x20, x11, lsl #2]
   96440:      	lsl	w12, w10, #1
   96444:      	and	w12, w12, #0x8
   96448:      	lsr	w12, w11, w12
   9644c:      	ldrb	w11, [x21, #0x2e]
   96450:      	ubfx	w10, w10, #3, #3
   96454:      	tbnz	w11, #0x6, 0x96464 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x50>
   96458:      	and	w11, w12, #0xff
   9645c:      	str	w11, [x20, w10, uxtw #2]
   96460:      	b	0x96470 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x5c>
   96464:      	ubfiz	x10, x10, #2, #32
   96468:      	and	w11, w12, #0xff
   9646c:      	strh	w11, [x20, x10]
   96470:      	ldp	w10, w11, [x21, #0x54]
   96474:      	and	x12, x10, #0x3f
   96478:      	ldr	w12, [x12, x20]
   9647c:      	ubfx	x13, x10, #6, #6
   96480:      	ldr	w13, [x13, x20]
   96484:      	ubfx	w14, w10, #12, #2
   96488:      	lsl	w13, w13, w14
   9648c:      	add	w11, w12, w11
   96490:      	add	w11, w11, w13
   96494:      	ldrsb	w12, [x21, #0x4e]
   96498:      	and	w13, w11, #0xffff
   9649c:      	cmp	w12, #0x0
   964a0:      	csel	w12, w13, w11, lt
   964a4:      	ubfx	x10, x10, #17, #3
   964a8:      	add	x11, x20, #0x34
   964ac:      	ldr	w10, [x11, x10, lsl #2]
   964b0:      	add	w10, w12, w10
   964b4:      	add	w12, w10, #0x3
   964b8:      	eor	w13, w12, w10
   964bc:      	and	w12, w10, #0xfffff000
   964c0:      	and	w13, w13, #0xfffff000
   964c4:      	eor	w14, w12, w23
   964c8:      	orr	w14, w13, w14
   964cc:      	cbnz	w14, 0x965a0 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x18c>
   964d0:      	ldr	w10, [x24, w10, uxtw]
   964d4:      	str	w10, [x20, #0x4]
   964d8:      	ldrb	w13, [x21, #0x6d]
   964dc:      	ubfx	x10, x13, #3, #3
   964e0:      	ldrb	w12, [x21, #0x6e]
   964e4:      	tbnz	w12, #0x6, 0x96514 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x100>
   964e8:      	cmp	w13, #0xc0
   964ec:      	b.lo	0x965e0 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x1cc>
   964f0:      	and	x8, x13, #0x7
   964f4:      	ldr	w11, [x20, x8, lsl #2]
   964f8:      	ldr	w8, [x20, w10, uxtw #2]
   964fc:      	eor	w8, w8, w11
   96500:      	str	w8, [x20, w10, uxtw #2]
   96504:      	mov	w8, w23
   96508:      	orr	x23, x8, x9, lsl #32
   9650c:      	ldr	x0, [x21, #0x80]!
   96510:      	br	x0
   96514:      	cmp	w13, #0xc0
   96518:      	b.lo	0x96644 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x230>
   9651c:      	and	x8, x13, #0x7
   96520:      	ldr	w11, [x20, x8, lsl #2]
   96524:      	lsl	x8, x10, #2
   96528:      	ldr	w10, [x20, x8]
   9652c:      	eor	w10, w11, w10
   96530:      	strh	w10, [x20, x8]
   96534:      	mov	w8, w23
   96538:      	orr	x23, x8, x9, lsl #32
   9653c:      	ldr	x0, [x21, #0x80]!
   96540:      	br	x0
   96544:      	ldp	w12, w11, [x21, #0x34]
   96548:      	and	x13, x12, #0x3f
   9654c:      	ldr	w13, [x13, x20]
   96550:      	ubfx	x14, x12, #6, #6
   96554:      	ldr	w14, [x14, x20]
   96558:      	ubfx	w15, w12, #12, #2
   9655c:      	lsl	w14, w14, w15
   96560:      	add	w11, w13, w11
   96564:      	add	w13, w14, w11
   96568:      	ldrb	w11, [x21, #0x2e]
   9656c:      	sxtb	w14, w11
   96570:      	and	w15, w13, #0xffff
   96574:      	cmp	w14, #0x0
   96578:      	csel	w13, w15, w13, lt
   9657c:      	ubfx	x12, x12, #17, #3
   96580:      	add	x12, x20, x12, lsl #2
   96584:      	ldr	w12, [x12, #0x34]
   96588:      	add	w12, w13, w12
   9658c:      	and	w13, w12, #0xfffff000
   96590:      	cmp	w13, w23
   96594:      	b.ne	0x966a8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x294>
   96598:      	ldrb	w12, [x24, w12, uxtw]
   9659c:      	b	0x96450 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3c>
   965a0:      	ubfx	w9, w10, #12, #8
   965a4:      	add	x9, x20, w9, uxtw #4
   965a8:      	ldr	w14, [x9, #0x1a0]
   965ac:      	eor	w14, w14, w12
   965b0:      	orr	w13, w14, w13
   965b4:      	cbnz	w13, 0x966e4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x2d0>
   965b8:      	add	x9, x9, #0x1a0
   965bc:      	ldr	x24, [x9, #0x8]
   965c0:      	ldr	w10, [x24, w10, uxtw]
   965c4:      	ldr	w9, [x9, #0x4]
   965c8:      	mov	w13, #0x2a              ; =42
   965cc:      	and	w9, w9, w13
   965d0:      	cmp	w9, #0xa
   965d4:      	csinv	w9, w12, wzr, eq
   965d8:      	mov	x23, x12
   965dc:      	b	0x964d4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xc0>
   965e0:      	ldp	w13, w14, [x21, #0x74]
   965e4:      	ubfx	x15, x13, #6, #6
   965e8:      	ldr	w15, [x15, x20]
   965ec:      	ubfx	w16, w13, #12, #2
   965f0:      	lsl	w15, w15, w16
   965f4:      	and	x16, x13, #0x3f
   965f8:      	ldr	w16, [x16, x20]
   965fc:      	add	w14, w16, w14
   96600:      	add	w14, w15, w14
   96604:      	and	w15, w14, #0xffff
   96608:      	sxtb	w12, w12
   9660c:      	cmp	w12, #0x0
   96610:      	csel	w12, w15, w14, lt
   96614:      	ubfx	x13, x13, #17, #3
   96618:      	ldr	w11, [x11, x13, lsl #2]
   9661c:      	add	w11, w12, w11
   96620:      	add	w12, w11, #0x3
   96624:      	eor	w13, w12, w11
   96628:      	and	w12, w11, #0xfffff000
   9662c:      	and	w13, w13, #0xfffff000
   96630:      	eor	w14, w12, w23
   96634:      	orr	w14, w13, w14
   96638:      	cbnz	w14, 0x96764 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x350>
   9663c:      	ldr	w11, [x24, w11, uxtw]
   96640:      	b	0x964f8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xe4>
   96644:      	ldp	w13, w14, [x21, #0x74]
   96648:      	ubfx	x15, x13, #6, #6
   9664c:      	ldr	w15, [x15, x20]
   96650:      	ubfx	w16, w13, #12, #2
   96654:      	lsl	w15, w15, w16
   96658:      	and	x16, x13, #0x3f
   9665c:      	ldr	w16, [x16, x20]
   96660:      	add	w14, w16, w14
   96664:      	add	w14, w15, w14
   96668:      	and	w15, w14, #0xffff
   9666c:      	sxtb	w12, w12
   96670:      	cmp	w12, #0x0
   96674:      	csel	w12, w15, w14, lt
   96678:      	ubfx	x13, x13, #17, #3
   9667c:      	ldr	w11, [x11, x13, lsl #2]
   96680:      	add	w11, w12, w11
   96684:      	add	w12, w11, #0x1
   96688:      	eor	w13, w12, w11
   9668c:      	and	w12, w11, #0xfffff000
   96690:      	and	w13, w13, #0xfffff000
   96694:      	eor	w14, w12, w23
   96698:      	orr	w14, w13, w14
   9669c:      	cbnz	w14, 0x967a4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x390>
   966a0:      	ldrh	w11, [x24, w11, uxtw]
   966a4:      	b	0x96524 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x110>
   966a8:      	ubfx	w9, w12, #12, #8
   966ac:      	add	x9, x20, w9, uxtw #4
   966b0:      	ldr	w14, [x9, #0x1a0]
   966b4:      	cmp	w14, w13
   966b8:      	b.ne	0x967e4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3d0>
   966bc:      	add	x9, x9, #0x1a0
   966c0:      	ldr	x24, [x9, #0x8]
   966c4:      	ldrb	w12, [x24, w12, uxtw]
   966c8:      	ldr	w9, [x9, #0x4]
   966cc:      	mov	w14, #0x2a              ; =42
   966d0:      	and	w9, w9, w14
   966d4:      	cmp	w9, #0xa
   966d8:      	csinv	w9, w13, wzr, eq
   966dc:      	mov	x23, x13
   966e0:      	b	0x96450 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3c>
   966e4:      	ldr	w9, [x21, #0x48]
   966e8:      	ldrb	w12, [x21, #0x4c]
   966ec:      	sub	w12, w9, w12
   966f0:      	str	w12, [x20, #0x24]
   966f4:      	ldrb	w12, [x8, #0x110]
   966f8:      	cmp	w12, #0x1
   966fc:      	b.ne	0x96730 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x31c>
   96700:      	ldrb	w12, [x8, #0x13c]
   96704:      	cmp	w12, #0x1
   96708:      	b.ne	0x96730 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x31c>
   9670c:      	ldr	w12, [x8, #0x138]
   96710:      	cmp	w12, w9
   96714:      	b.ne	0x96730 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x31c>
   96718:      	mov	x24, #0x0               ; =0
   9671c:      	ldr	w10, [x8, #0x120]
   96720:      	strb	wzr, [x8, #0x110]
   96724:      	mov	w9, #-0x1               ; =-1
   96728:      	mov	x23, x9
   9672c:      	b	0x964d4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xc0>
   96730:      	mov	w11, #0x1               ; =1
   96734:      	strb	w11, [x8, #0x110]
   96738:      	movi.2d	v0, #0000000000000000
   9673c:      	str	q0, [x20, #0x7120]
   96740:      	str	w10, [x8, #0x130]
   96744:      	mov	w10, #0x4               ; =4
   96748:      	str	w10, [x8, #0x134]
   9674c:      	str	w9, [x8, #0x138]
   96750:      	strb	wzr, [x8, #0x13c]
   96754:      	add	x21, x21, #0x40
   96758:      	mov	x23, #-0x1              ; =-1
   9675c:      	mov	x24, #0x0               ; =0
   96760:      	b	0x140a1c <_vsnprintf+0x140a1c>
   96764:      	ubfx	w9, w11, #12, #8
   96768:      	add	x9, x20, w9, uxtw #4
   9676c:      	ldr	w14, [x9, #0x1a0]
   96770:      	eor	w14, w14, w12
   96774:      	orr	w13, w14, w13
   96778:      	cbnz	w13, 0x96860 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x44c>
   9677c:      	add	x8, x9, #0x1a0
   96780:      	ldr	x24, [x8, #0x8]
   96784:      	ldr	w11, [x24, w11, uxtw]
   96788:      	ldr	w8, [x8, #0x4]
   9678c:      	mov	w9, #0x2a               ; =42
   96790:      	and	w8, w8, w9
   96794:      	cmp	w8, #0xa
   96798:      	csinv	w9, w12, wzr, eq
   9679c:      	mov	x23, x12
   967a0:      	b	0x964f8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xe4>
   967a4:      	ubfx	w9, w11, #12, #8
   967a8:      	add	x9, x20, w9, uxtw #4
   967ac:      	ldr	w14, [x9, #0x1a0]
   967b0:      	eor	w14, w14, w12
   967b4:      	orr	w13, w14, w13
   967b8:      	cbnz	w13, 0x968ac <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x498>
   967bc:      	add	x8, x9, #0x1a0
   967c0:      	ldr	x24, [x8, #0x8]
   967c4:      	ldrh	w11, [x24, w11, uxtw]
   967c8:      	ldr	w8, [x8, #0x4]
   967cc:      	mov	w9, #0x2a               ; =42
   967d0:      	and	w8, w8, w9
   967d4:      	cmp	w8, #0xa
   967d8:      	csinv	w9, w12, wzr, eq
   967dc:      	mov	x23, x12
   967e0:      	b	0x96524 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x110>
   967e4:      	ldr	w9, [x21, #0x28]
   967e8:      	ldrb	w13, [x21, #0x2c]
   967ec:      	sub	w13, w9, w13
   967f0:      	str	w13, [x20, #0x24]
   967f4:      	ldrb	w13, [x8, #0x110]
   967f8:      	cmp	w13, #0x1
   967fc:      	b.ne	0x96830 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x41c>
   96800:      	ldrb	w13, [x8, #0x13c]
   96804:      	cmp	w13, #0x1
   96808:      	b.ne	0x96830 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x41c>
   9680c:      	ldr	w13, [x8, #0x138]
   96810:      	cmp	w13, w9
   96814:      	b.ne	0x96830 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x41c>
   96818:      	mov	x24, #0x0               ; =0
   9681c:      	ldrb	w12, [x8, #0x120]
   96820:      	strb	wzr, [x8, #0x110]
   96824:      	mov	w9, #-0x1               ; =-1
   96828:      	mov	x23, x9
   9682c:      	b	0x96450 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3c>
   96830:      	mov	w10, #0x1               ; =1
   96834:      	strb	w10, [x8, #0x110]
   96838:      	movi.2d	v0, #0000000000000000
   9683c:      	str	q0, [x20, #0x7120]
   96840:      	str	w12, [x8, #0x130]
   96844:      	str	w10, [x8, #0x134]
   96848:      	str	w9, [x8, #0x138]
   9684c:      	strb	wzr, [x8, #0x13c]
   96850:      	add	x21, x21, #0x20
   96854:      	mov	x23, #-0x1              ; =-1
   96858:      	mov	x24, #0x0               ; =0
   9685c:      	b	0x140a1c <_vsnprintf+0x140a1c>
   96860:      	ldr	w9, [x21, #0x68]
   96864:      	ldrb	w12, [x21, #0x6c]
   96868:      	sub	w12, w9, w12
   9686c:      	str	w12, [x20, #0x24]
   96870:      	ldrb	w12, [x8, #0x110]
   96874:      	cmp	w12, #0x1
   96878:      	b.ne	0x96900 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4ec>
   9687c:      	ldrb	w12, [x8, #0x13c]
   96880:      	cmp	w12, #0x1
   96884:      	b.ne	0x96900 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4ec>
   96888:      	ldr	w12, [x8, #0x138]
   9688c:      	cmp	w12, w9
   96890:      	b.ne	0x96900 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4ec>
   96894:      	mov	x24, #0x0               ; =0
   96898:      	ldr	w11, [x8, #0x120]
   9689c:      	strb	wzr, [x8, #0x110]
   968a0:      	mov	w9, #-0x1               ; =-1
   968a4:      	mov	x23, x9
   968a8:      	b	0x964f8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xe4>
   968ac:      	ldr	w9, [x21, #0x68]
   968b0:      	ldrb	w12, [x21, #0x6c]
   968b4:      	sub	w12, w9, w12
   968b8:      	str	w12, [x20, #0x24]
   968bc:      	ldrb	w12, [x8, #0x110]
   968c0:      	cmp	w12, #0x1
   968c4:      	b.ne	0x968f8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4e4>
   968c8:      	ldrb	w12, [x8, #0x13c]
   968cc:      	cmp	w12, #0x1
   968d0:      	b.ne	0x968f8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4e4>
   968d4:      	ldr	w12, [x8, #0x138]
   968d8:      	cmp	w12, w9
   968dc:      	b.ne	0x968f8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4e4>
   968e0:      	mov	x24, #0x0               ; =0
   968e4:      	ldrh	w11, [x8, #0x120]
   968e8:      	strb	wzr, [x8, #0x110]
   968ec:      	mov	w9, #-0x1               ; =-1
   968f0:      	mov	x23, x9
   968f4:      	b	0x96524 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x110>
   968f8:      	mov	w10, #0x2               ; =2
   968fc:      	b	0x96904 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4f0>
   96900:      	mov	w10, #0x4               ; =4
   96904:      	mov	w12, #0x1               ; =1
   96908:      	strb	w12, [x8, #0x110]
   9690c:      	movi.2d	v0, #0000000000000000
   96910:      	str	q0, [x20, #0x7120]
   96914:      	str	w11, [x8, #0x130]
   96918:      	str	w10, [x8, #0x134]
   9691c:      	str	w9, [x8, #0x138]
   96920:      	strb	wzr, [x8, #0x13c]
   96924:      	add	x21, x21, #0x60
   96928:      	mov	x23, #-0x1              ; =-1
   9692c:      	mov	x24, #0x0               ; =0
   96930:      	b	0x140a1c <_vsnprintf+0x140a1c>
