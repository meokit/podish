
/Users/jiangyiheng/repos/x86emu/artifacts/pin-gnu-20261118/off-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000000ae87c <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy>:
   ae87c:      	add	x8, x20, #0x7, lsl #12  ; =0x7000
   ae880:      	lsr	x9, x23, #32
   ae884:      	ldrb	w10, [x21, #0xd]
   ae888:      	cmp	w10, #0xc0
   ae88c:      	b.lo	0xaea08 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x18c>
   ae890:      	and	x11, x10, #0x3
   ae894:      	ldr	w11, [x20, x11, lsl #2]
   ae898:      	lsl	w12, w10, #1
   ae89c:      	and	w12, w12, #0x8
   ae8a0:      	lsr	w12, w11, w12
   ae8a4:      	ldrb	w11, [x21, #0xe]
   ae8a8:      	ubfx	w10, w10, #3, #3
   ae8ac:      	tbnz	w11, #0x6, 0xae8bc <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x40>
   ae8b0:      	and	w11, w12, #0xff
   ae8b4:      	str	w11, [x20, w10, uxtw #2]
   ae8b8:      	b	0xae8c8 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4c>
   ae8bc:      	ubfiz	x10, x10, #2, #32
   ae8c0:      	and	w11, w12, #0xff
   ae8c4:      	strh	w11, [x20, x10]
   ae8c8:      	ldrb	w10, [x21, #0x2d]
   ae8cc:      	and	x10, x10, #0x7
   ae8d0:      	ldrb	w11, [x21, #0x2e]
   ae8d4:      	ldr	w12, [x20, x10, lsl #2]
   ae8d8:      	add	w13, w12, #0x1
   ae8dc:      	mov	x14, x12
   ae8e0:      	bfxil	w14, w13, #0, #16
   ae8e4:      	tst	w11, #0x40
   ae8e8:      	csinc	w11, w14, w12, ne
   ae8ec:      	str	w11, [x20, x10, lsl #2]
   ae8f0:      	ldrb	w10, [x21, #0x4d]
   ae8f4:      	ubfx	x11, x10, #3, #3
   ae8f8:      	and	x10, x10, #0x7
   ae8fc:      	ldr	w12, [x20, x10, lsl #2]
   ae900:      	ldr	w11, [x20, x11, lsl #2]
   ae904:      	eor	w11, w11, w12
   ae908:      	str	w11, [x20, x10, lsl #2]
   ae90c:      	ldrb	w10, [x21, #0x6f]
   ae910:      	tbz	w10, #0x2, 0xae984 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x108>
   ae914:      	ldr	w11, [x21, #0x70]
   ae918:      	and	w14, w11, #0x1f
   ae91c:      	ldrb	w13, [x21, #0x6d]
   ae920:      	and	x12, x13, #0x7
   ae924:      	ldrb	w10, [x21, #0x6e]
   ae928:      	tbnz	w10, #0x6, 0xae994 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x118>
   ae92c:      	cbz	w14, 0xae984 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x108>
   ae930:      	ldr	w15, [x20, x12, lsl #2]
   ae934:      	and	w16, w11, #0x1f
   ae938:      	sub	w16, w16, #0x1
   ae93c:      	lsr	w16, w15, w16
   ae940:      	lsr	w11, w15, w11
   ae944:      	mov	x17, #-0x8d6            ; =-2262
   ae948:      	and	x17, x26, x17
   ae94c:      	orr	x19, x17, #0x40
   ae950:      	cmp	w11, #0x0
   ae954:      	csel	x17, x19, x17, eq
   ae958:      	bfxil	x17, x16, #0, #1
   ae95c:      	cmp	w15, #0x0
   ae960:      	ccmp	w14, #0x1, #0x0, lt
   ae964:      	orr	x14, x17, #0x800
   ae968:      	csel	x14, x14, x17, eq
   ae96c:      	and	x14, x14, #0xffff00ffffffffff
   ae970:      	and	w15, w11, #0xff
   ae974:      	orr	x26, x14, x15, lsl #40
   ae978:      	cmp	w13, #0xbf
   ae97c:      	b.ls	0xaea64 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x1e8>
   ae980:      	str	w11, [x20, x12, lsl #2]
   ae984:      	mov	w8, w23
   ae988:      	orr	x23, x8, x9, lsl #32
   ae98c:      	ldr	x0, [x21, #0x80]!
   ae990:      	br	x0
   ae994:      	cbz	w14, 0xae984 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x108>
   ae998:      	ldr	w15, [x20, w12, uxtw #2]
   ae99c:      	and	w16, w11, #0x1f
   ae9a0:      	and	w17, w15, #0xffff
   ae9a4:      	sub	w16, w16, #0x1
   ae9a8:      	lsr	w16, w17, w16
   ae9ac:      	lsr	w11, w17, w11
   ae9b0:      	mov	x17, #-0x8d6            ; =-2262
   ae9b4:      	and	x17, x26, x17
   ae9b8:      	orr	x19, x17, #0x40
   ae9bc:      	cmp	w11, #0x0
   ae9c0:      	csel	x17, x19, x17, eq
   ae9c4:      	bfxil	x17, x16, #0, #1
   ae9c8:      	tst	w15, #0x8000
   ae9cc:      	ccmp	w14, #0x1, #0x0, ne
   ae9d0:      	orr	x14, x17, #0x800
   ae9d4:      	csel	x14, x17, x14, ne
   ae9d8:      	and	x14, x14, #0xffff00ffffffffff
   ae9dc:      	and	w16, w11, #0xff
   ae9e0:      	orr	x26, x14, x16, lsl #40
   ae9e4:      	cmp	w13, #0xbf
   ae9e8:      	b.ls	0xaead8 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x25c>
   ae9ec:      	and	w8, w15, #0xffff0000
   ae9f0:      	orr	w8, w11, w8
   ae9f4:      	str	w8, [x20, w12, uxtw #2]
   ae9f8:      	mov	w8, w23
   ae9fc:      	orr	x23, x8, x9, lsl #32
   aea00:      	ldr	x0, [x21, #0x80]!
   aea04:      	br	x0
   aea08:      	ldp	w12, w11, [x21, #0x14]
   aea0c:      	and	x13, x12, #0x3f
   aea10:      	ldr	w13, [x13, x20]
   aea14:      	ubfx	x14, x12, #6, #6
   aea18:      	ldr	w14, [x14, x20]
   aea1c:      	ubfx	w15, w12, #12, #2
   aea20:      	lsl	w14, w14, w15
   aea24:      	add	w11, w13, w11
   aea28:      	add	w13, w14, w11
   aea2c:      	ldrb	w11, [x21, #0xe]
   aea30:      	sxtb	w14, w11
   aea34:      	and	w15, w13, #0xffff
   aea38:      	cmp	w14, #0x0
   aea3c:      	csel	w13, w15, w13, lt
   aea40:      	ubfx	x12, x12, #17, #3
   aea44:      	add	x12, x20, x12, lsl #2
   aea48:      	ldr	w12, [x12, #0x34]
   aea4c:      	add	w12, w13, w12
   aea50:      	and	w13, w12, #0xfffff000
   aea54:      	cmp	w13, w23
   aea58:      	b.ne	0xaeb4c <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x2d0>
   aea5c:      	ldrb	w12, [x24, w12, uxtw]
   aea60:      	b	0xae8a8 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x2c>
   aea64:      	ldp	w12, w13, [x21, #0x74]
   aea68:      	ubfx	x14, x12, #6, #6
   aea6c:      	ldr	w14, [x14, x20]
   aea70:      	ubfx	w15, w12, #12, #2
   aea74:      	lsl	w14, w14, w15
   aea78:      	and	x15, x12, #0x3f
   aea7c:      	ldr	w15, [x15, x20]
   aea80:      	add	w13, w15, w13
   aea84:      	add	w13, w14, w13
   aea88:      	and	w14, w13, #0xffff
   aea8c:      	sxtb	w10, w10
   aea90:      	cmp	w10, #0x0
   aea94:      	csel	w10, w14, w13, lt
   aea98:      	ubfx	x12, x12, #17, #3
   aea9c:      	add	x12, x20, x12, lsl #2
   aeaa0:      	ldr	w12, [x12, #0x34]
   aeaa4:      	add	w10, w10, w12
   aeaa8:      	add	w12, w10, #0x3
   aeaac:      	eor	w13, w12, w10
   aeab0:      	and	w12, w10, #0xfffff000
   aeab4:      	and	w13, w13, #0xfffff000
   aeab8:      	eor	w14, w12, w9
   aeabc:      	orr	w14, w13, w14
   aeac0:      	cbnz	w14, 0xaeb88 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x30c>
   aeac4:      	str	w11, [x24, w10, uxtw]
   aeac8:      	mov	w8, w23
   aeacc:      	orr	x23, x8, x9, lsl #32
   aead0:      	ldr	x0, [x21, #0x80]!
   aead4:      	br	x0
   aead8:      	ldp	w12, w13, [x21, #0x74]
   aeadc:      	ubfx	x14, x12, #6, #6
   aeae0:      	ldr	w14, [x14, x20]
   aeae4:      	ubfx	w15, w12, #12, #2
   aeae8:      	lsl	w14, w14, w15
   aeaec:      	and	x15, x12, #0x3f
   aeaf0:      	ldr	w15, [x15, x20]
   aeaf4:      	add	w13, w15, w13
   aeaf8:      	add	w13, w14, w13
   aeafc:      	and	w14, w13, #0xffff
   aeb00:      	sxtb	w10, w10
   aeb04:      	cmp	w10, #0x0
   aeb08:      	csel	w10, w14, w13, lt
   aeb0c:      	ubfx	x12, x12, #17, #3
   aeb10:      	add	x12, x20, x12, lsl #2
   aeb14:      	ldr	w12, [x12, #0x34]
   aeb18:      	add	w10, w10, w12
   aeb1c:      	add	w12, w10, #0x1
   aeb20:      	eor	w13, w12, w10
   aeb24:      	and	w12, w10, #0xfffff000
   aeb28:      	and	w13, w13, #0xfffff000
   aeb2c:      	eor	w14, w12, w9
   aeb30:      	orr	w14, w13, w14
   aeb34:      	cbnz	w14, 0xaebb8 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x33c>
   aeb38:      	strh	w11, [x24, w10, uxtw]
   aeb3c:      	mov	w8, w23
   aeb40:      	orr	x23, x8, x9, lsl #32
   aeb44:      	ldr	x0, [x21, #0x80]!
   aeb48:      	br	x0
   aeb4c:      	ubfx	w9, w12, #12, #8
   aeb50:      	add	x9, x20, w9, uxtw #4
   aeb54:      	ldr	w14, [x9, #0x1a0]
   aeb58:      	cmp	w14, w13
   aeb5c:      	b.ne	0xaebfc <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x380>
   aeb60:      	add	x9, x9, #0x1a0
   aeb64:      	ldr	x24, [x9, #0x8]
   aeb68:      	ldrb	w12, [x24, w12, uxtw]
   aeb6c:      	ldr	w9, [x9, #0x4]
   aeb70:      	mov	w14, #0x2a              ; =42
   aeb74:      	and	w9, w9, w14
   aeb78:      	cmp	w9, #0xa
   aeb7c:      	csinv	w9, w13, wzr, eq
   aeb80:      	mov	x23, x13
   aeb84:      	b	0xae8a8 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x2c>
   aeb88:      	ubfx	w9, w10, #12, #8
   aeb8c:      	add	x9, x20, w9, uxtw #4
   aeb90:      	ldr	w14, [x9, #0x11a0]
   aeb94:      	eor	w14, w14, w12
   aeb98:      	orr	w13, w14, w13
   aeb9c:      	cbnz	w13, 0xaec74 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3f8>
   aeba0:      	mov	w8, #0x11a0             ; =4512
   aeba4:      	add	x8, x9, x8
   aeba8:      	ldr	x24, [x8, #0x8]
   aebac:      	ldr	w8, [x8, #0x4]
   aebb0:      	str	w11, [x24, w10, uxtw]
   aebb4:      	b	0xaebe4 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x368>
   aebb8:      	ubfx	w9, w10, #12, #8
   aebbc:      	add	x9, x20, w9, uxtw #4
   aebc0:      	ldr	w14, [x9, #0x11a0]
   aebc4:      	eor	w14, w14, w12
   aebc8:      	orr	w13, w14, w13
   aebcc:      	cbnz	w13, 0xaecb4 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x438>
   aebd0:      	mov	w8, #0x11a0             ; =4512
   aebd4:      	add	x8, x9, x8
   aebd8:      	ldr	x24, [x8, #0x8]
   aebdc:      	ldr	w8, [x8, #0x4]
   aebe0:      	strh	w11, [x24, w10, uxtw]
   aebe4:      	tst	w8, #0x1
   aebe8:      	csinv	w23, w12, wzr, ne
   aebec:      	mov	w8, w23
   aebf0:      	orr	x23, x8, x12, lsl #32
   aebf4:      	ldr	x0, [x21, #0x80]!
   aebf8:      	br	x0
   aebfc:      	ldr	w9, [x21, #0x8]
   aec00:      	ldrb	w13, [x21, #0xc]
   aec04:      	sub	w13, w9, w13
   aec08:      	str	w13, [x20, #0x24]
   aec0c:      	ldrb	w13, [x8, #0x110]
   aec10:      	cmp	w13, #0x1
   aec14:      	b.ne	0xaec48 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3cc>
   aec18:      	ldrb	w13, [x8, #0x13c]
   aec1c:      	cmp	w13, #0x1
   aec20:      	b.ne	0xaec48 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3cc>
   aec24:      	ldr	w13, [x8, #0x138]
   aec28:      	cmp	w13, w9
   aec2c:      	b.ne	0xaec48 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3cc>
   aec30:      	mov	x24, #0x0               ; =0
   aec34:      	ldrb	w12, [x8, #0x120]
   aec38:      	strb	wzr, [x8, #0x110]
   aec3c:      	mov	w9, #-0x1               ; =-1
   aec40:      	mov	x23, x9
   aec44:      	b	0xae8a8 <__ZN8fiberish95SuperOpcode_4_162_OpMovzx_Byte__OpInc_Reg_NF__OpXor_EvGv_NF_32_ModReg__OpGroup2_EvIb_Shr_ModRegEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x2c>
   aec48:      	mov	w10, #0x1               ; =1
   aec4c:      	strb	w10, [x8, #0x110]
   aec50:      	movi.2d	v0, #0000000000000000
   aec54:      	str	q0, [x20, #0x7120]
   aec58:      	str	w12, [x8, #0x130]
   aec5c:      	str	w10, [x8, #0x134]
   aec60:      	str	w9, [x8, #0x138]
   aec64:      	strb	wzr, [x8, #0x13c]
   aec68:      	mov	x23, #-0x1              ; =-1
   aec6c:      	mov	x24, #0x0               ; =0
   aec70:      	b	0x140a1c <_vsnprintf+0x140a1c>
   aec74:      	ldr	w9, [x21, #0x68]
   aec78:      	ldrb	w12, [x21, #0x6c]
   aec7c:      	sub	w12, w9, w12
   aec80:      	str	w12, [x20, #0x24]
   aec84:      	mov	w12, #0x2               ; =2
   aec88:      	strb	w12, [x8, #0x110]
   aec8c:      	str	w10, [x8, #0x130]
   aec90:      	mov	w10, #0x4               ; =4
   aec94:      	str	w10, [x8, #0x134]
   aec98:      	strb	wzr, [x8, #0x13c]
   aec9c:      	str	w9, [x8, #0x138]
   aeca0:      	str	w11, [x8, #0x120]
   aeca4:      	add	x21, x21, #0x60
   aeca8:      	mov	x23, #-0x1              ; =-1
   aecac:      	mov	x24, #0x0               ; =0
   aecb0:      	b	0x140a10 <_vsnprintf+0x140a10>
   aecb4:      	ldr	w9, [x21, #0x68]
   aecb8:      	ldrb	w12, [x21, #0x6c]
   aecbc:      	sub	w12, w9, w12
   aecc0:      	str	w12, [x20, #0x24]
   aecc4:      	mov	w12, #0x2               ; =2
   aecc8:      	strb	w12, [x8, #0x110]
   aeccc:      	str	w10, [x8, #0x130]
   aecd0:      	str	w12, [x8, #0x134]
   aecd4:      	strb	wzr, [x8, #0x13c]
   aecd8:      	str	w9, [x8, #0x138]
   aecdc:      	strh	w11, [x8, #0x120]
   aece0:      	add	x21, x21, #0x60
   aece4:      	mov	x23, #-0x1              ; =-1
   aece8:      	mov	x24, #0x0               ; =0
   aecec:      	b	0x140a10 <_vsnprintf+0x140a10>
