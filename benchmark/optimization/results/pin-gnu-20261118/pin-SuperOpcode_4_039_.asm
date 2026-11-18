
/Users/jiangyiheng/repos/x86emu/artifacts/pin-gnu-20261118/pin-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

000000000009778c <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>:
   9778c:      	add	x8, x20, #0x7, lsl #12  ; =0x7000
   97790:      	lsr	x9, x23, #32
   97794:      	ldr	w10, [x20, #0x18]
   97798:      	ldrb	w11, [x21, #0xd]
   9779c:      	and	x11, x11, #0x7
   977a0:      	str	w10, [x20, x11, lsl #2]
   977a4:      	ldrb	w10, [x21, #0x2d]
   977a8:      	cmp	w10, #0xc0
   977ac:      	b.lo	0x978cc <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x140>
   977b0:      	and	x11, x10, #0x3
   977b4:      	ldr	w11, [x20, x11, lsl #2]
   977b8:      	lsl	w12, w10, #1
   977bc:      	and	w12, w12, #0x8
   977c0:      	lsr	w12, w11, w12
   977c4:      	ldrb	w11, [x21, #0x2e]
   977c8:      	ubfx	w10, w10, #3, #3
   977cc:      	tbnz	w11, #0x6, 0x977dc <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x50>
   977d0:      	and	w11, w12, #0xff
   977d4:      	str	w11, [x20, w10, uxtw #2]
   977d8:      	b	0x977e8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x5c>
   977dc:      	ubfiz	x10, x10, #2, #32
   977e0:      	and	w11, w12, #0xff
   977e4:      	strh	w11, [x20, x10]
   977e8:      	ldp	w10, w11, [x21, #0x54]
   977ec:      	and	x12, x10, #0x3f
   977f0:      	ldr	w12, [x12, x20]
   977f4:      	ubfx	x13, x10, #6, #6
   977f8:      	ldr	w13, [x13, x20]
   977fc:      	ubfx	w14, w10, #12, #2
   97800:      	lsl	w13, w13, w14
   97804:      	add	w11, w12, w11
   97808:      	add	w11, w11, w13
   9780c:      	ldrsb	w12, [x21, #0x4e]
   97810:      	and	w13, w11, #0xffff
   97814:      	cmp	w12, #0x0
   97818:      	csel	w12, w13, w11, lt
   9781c:      	ubfx	x10, x10, #17, #3
   97820:      	add	x11, x20, #0x34
   97824:      	ldr	w10, [x11, x10, lsl #2]
   97828:      	add	w10, w12, w10
   9782c:      	add	w12, w10, #0x3
   97830:      	eor	w13, w12, w10
   97834:      	and	w12, w10, #0xfffff000
   97838:      	and	w13, w13, #0xfffff000
   9783c:      	eor	w14, w12, w23
   97840:      	orr	w14, w13, w14
   97844:      	cbnz	w14, 0x97928 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x19c>
   97848:      	ldr	w10, [x24, w10, uxtw]
   9784c:      	str	w10, [x20, #0x4]
   97850:      	ldrb	w13, [x21, #0x6d]
   97854:      	ubfx	x10, x13, #3, #3
   97858:      	ldrb	w12, [x21, #0x6e]
   9785c:      	tbnz	w12, #0x6, 0x97894 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x108>
   97860:      	cmp	w13, #0xc0
   97864:      	b.lo	0x97968 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1dc>
   97868:      	and	x8, x13, #0x7
   9786c:      	ldr	w11, [x20, x8, lsl #2]
   97870:      	ldr	w8, [x20, w10, uxtw #2]
   97874:      	eor	w8, w8, w11
   97878:      	str	w8, [x20, w10, uxtw #2]
   9787c:      	mov	w8, w23
   97880:      	ldr	x0, [x21, #0x80]!
   97884:      	orr	x23, x8, x9, lsl #32
   97888:      	ldr	w27, [x20]
   9788c:      	ldr	w28, [x20, #0x10]
   97890:      	br	x0
   97894:      	cmp	w13, #0xc0
   97898:      	b.lo	0x979cc <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x240>
   9789c:      	and	x8, x13, #0x7
   978a0:      	ldr	w11, [x20, x8, lsl #2]
   978a4:      	lsl	x8, x10, #2
   978a8:      	ldr	w10, [x20, x8]
   978ac:      	eor	w10, w11, w10
   978b0:      	strh	w10, [x20, x8]
   978b4:      	mov	w8, w23
   978b8:      	ldr	x0, [x21, #0x80]!
   978bc:      	orr	x23, x8, x9, lsl #32
   978c0:      	ldr	w27, [x20]
   978c4:      	ldr	w28, [x20, #0x10]
   978c8:      	br	x0
   978cc:      	ldp	w12, w11, [x21, #0x34]
   978d0:      	and	x13, x12, #0x3f
   978d4:      	ldr	w13, [x13, x20]
   978d8:      	ubfx	x14, x12, #6, #6
   978dc:      	ldr	w14, [x14, x20]
   978e0:      	ubfx	w15, w12, #12, #2
   978e4:      	lsl	w14, w14, w15
   978e8:      	add	w11, w13, w11
   978ec:      	add	w13, w14, w11
   978f0:      	ldrb	w11, [x21, #0x2e]
   978f4:      	sxtb	w14, w11
   978f8:      	and	w15, w13, #0xffff
   978fc:      	cmp	w14, #0x0
   97900:      	csel	w13, w15, w13, lt
   97904:      	ubfx	x12, x12, #17, #3
   97908:      	add	x12, x20, x12, lsl #2
   9790c:      	ldr	w12, [x12, #0x34]
   97910:      	add	w12, w13, w12
   97914:      	and	w13, w12, #0xfffff000
   97918:      	cmp	w13, w23
   9791c:      	b.ne	0x97a30 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x2a4>
   97920:      	ldrb	w12, [x24, w12, uxtw]
   97924:      	b	0x977c8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3c>
   97928:      	ubfx	w9, w10, #12, #8
   9792c:      	add	x9, x20, w9, uxtw #4
   97930:      	ldr	w14, [x9, #0x1a0]
   97934:      	eor	w14, w14, w12
   97938:      	orr	w13, w14, w13
   9793c:      	cbnz	w13, 0x97a6c <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x2e0>
   97940:      	add	x9, x9, #0x1a0
   97944:      	ldr	x24, [x9, #0x8]
   97948:      	ldr	w10, [x24, w10, uxtw]
   9794c:      	ldr	w9, [x9, #0x4]
   97950:      	mov	w13, #0x2a              ; =42
   97954:      	and	w9, w9, w13
   97958:      	cmp	w9, #0xa
   9795c:      	csinv	w9, w12, wzr, eq
   97960:      	mov	x23, x12
   97964:      	b	0x9784c <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xc0>
   97968:      	ldp	w13, w14, [x21, #0x74]
   9796c:      	ubfx	x15, x13, #6, #6
   97970:      	ldr	w15, [x15, x20]
   97974:      	ubfx	w16, w13, #12, #2
   97978:      	lsl	w15, w15, w16
   9797c:      	and	x16, x13, #0x3f
   97980:      	ldr	w16, [x16, x20]
   97984:      	add	w14, w16, w14
   97988:      	add	w14, w15, w14
   9798c:      	and	w15, w14, #0xffff
   97990:      	sxtb	w12, w12
   97994:      	cmp	w12, #0x0
   97998:      	csel	w12, w15, w14, lt
   9799c:      	ubfx	x13, x13, #17, #3
   979a0:      	ldr	w11, [x11, x13, lsl #2]
   979a4:      	add	w11, w12, w11
   979a8:      	add	w12, w11, #0x3
   979ac:      	eor	w13, w12, w11
   979b0:      	and	w12, w11, #0xfffff000
   979b4:      	and	w13, w13, #0xfffff000
   979b8:      	eor	w14, w12, w23
   979bc:      	orr	w14, w13, w14
   979c0:      	cbnz	w14, 0x97af4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x368>
   979c4:      	ldr	w11, [x24, w11, uxtw]
   979c8:      	b	0x97870 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xe4>
   979cc:      	ldp	w13, w14, [x21, #0x74]
   979d0:      	ubfx	x15, x13, #6, #6
   979d4:      	ldr	w15, [x15, x20]
   979d8:      	ubfx	w16, w13, #12, #2
   979dc:      	lsl	w15, w15, w16
   979e0:      	and	x16, x13, #0x3f
   979e4:      	ldr	w16, [x16, x20]
   979e8:      	add	w14, w16, w14
   979ec:      	add	w14, w15, w14
   979f0:      	and	w15, w14, #0xffff
   979f4:      	sxtb	w12, w12
   979f8:      	cmp	w12, #0x0
   979fc:      	csel	w12, w15, w14, lt
   97a00:      	ubfx	x13, x13, #17, #3
   97a04:      	ldr	w11, [x11, x13, lsl #2]
   97a08:      	add	w11, w12, w11
   97a0c:      	add	w12, w11, #0x1
   97a10:      	eor	w13, w12, w11
   97a14:      	and	w12, w11, #0xfffff000
   97a18:      	and	w13, w13, #0xfffff000
   97a1c:      	eor	w14, w12, w23
   97a20:      	orr	w14, w13, w14
   97a24:      	cbnz	w14, 0x97b34 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3a8>
   97a28:      	ldrh	w11, [x24, w11, uxtw]
   97a2c:      	b	0x978a4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
   97a30:      	ubfx	w9, w12, #12, #8
   97a34:      	add	x9, x20, w9, uxtw #4
   97a38:      	ldr	w14, [x9, #0x1a0]
   97a3c:      	cmp	w14, w13
   97a40:      	b.ne	0x97b74 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3e8>
   97a44:      	add	x9, x9, #0x1a0
   97a48:      	ldr	x24, [x9, #0x8]
   97a4c:      	ldrb	w12, [x24, w12, uxtw]
   97a50:      	ldr	w9, [x9, #0x4]
   97a54:      	mov	w14, #0x2a              ; =42
   97a58:      	and	w9, w9, w14
   97a5c:      	cmp	w9, #0xa
   97a60:      	csinv	w9, w13, wzr, eq
   97a64:      	mov	x23, x13
   97a68:      	b	0x977c8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3c>
   97a6c:      	ldr	w9, [x21, #0x48]
   97a70:      	ldrb	w12, [x21, #0x4c]
   97a74:      	sub	w12, w9, w12
   97a78:      	str	w12, [x20, #0x24]
   97a7c:      	ldrb	w12, [x8, #0x110]
   97a80:      	cmp	w12, #0x1
   97a84:      	b.ne	0x97ab8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x32c>
   97a88:      	ldrb	w12, [x8, #0x13c]
   97a8c:      	cmp	w12, #0x1
   97a90:      	b.ne	0x97ab8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x32c>
   97a94:      	ldr	w12, [x8, #0x138]
   97a98:      	cmp	w12, w9
   97a9c:      	b.ne	0x97ab8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x32c>
   97aa0:      	mov	x24, #0x0               ; =0
   97aa4:      	ldr	w10, [x8, #0x120]
   97aa8:      	strb	wzr, [x8, #0x110]
   97aac:      	mov	w9, #-0x1               ; =-1
   97ab0:      	mov	x23, x9
   97ab4:      	b	0x9784c <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xc0>
   97ab8:      	mov	w11, #0x1               ; =1
   97abc:      	strb	w11, [x8, #0x110]
   97ac0:      	movi.2d	v0, #0000000000000000
   97ac4:      	str	q0, [x20, #0x7120]
   97ac8:      	str	w10, [x8, #0x130]
   97acc:      	mov	w10, #0x4               ; =4
   97ad0:      	str	w10, [x8, #0x134]
   97ad4:      	str	w9, [x8, #0x138]
   97ad8:      	strb	wzr, [x8, #0x13c]
   97adc:      	ldr	w27, [x20]
   97ae0:      	ldr	w28, [x20, #0x10]
   97ae4:      	add	x21, x21, #0x40
   97ae8:      	mov	x23, #-0x1              ; =-1
   97aec:      	mov	x24, #0x0               ; =0
   97af0:      	b	0x144268 <_vsnprintf+0x144268>
   97af4:      	ubfx	w9, w11, #12, #8
   97af8:      	add	x9, x20, w9, uxtw #4
   97afc:      	ldr	w14, [x9, #0x1a0]
   97b00:      	eor	w14, w14, w12
   97b04:      	orr	w13, w14, w13
   97b08:      	cbnz	w13, 0x97bf8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x46c>
   97b0c:      	add	x8, x9, #0x1a0
   97b10:      	ldr	x24, [x8, #0x8]
   97b14:      	ldr	w11, [x24, w11, uxtw]
   97b18:      	ldr	w8, [x8, #0x4]
   97b1c:      	mov	w9, #0x2a               ; =42
   97b20:      	and	w8, w8, w9
   97b24:      	cmp	w8, #0xa
   97b28:      	csinv	w9, w12, wzr, eq
   97b2c:      	mov	x23, x12
   97b30:      	b	0x97870 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xe4>
   97b34:      	ubfx	w9, w11, #12, #8
   97b38:      	add	x9, x20, w9, uxtw #4
   97b3c:      	ldr	w14, [x9, #0x1a0]
   97b40:      	eor	w14, w14, w12
   97b44:      	orr	w13, w14, w13
   97b48:      	cbnz	w13, 0x97c44 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x4b8>
   97b4c:      	add	x8, x9, #0x1a0
   97b50:      	ldr	x24, [x8, #0x8]
   97b54:      	ldrh	w11, [x24, w11, uxtw]
   97b58:      	ldr	w8, [x8, #0x4]
   97b5c:      	mov	w9, #0x2a               ; =42
   97b60:      	and	w8, w8, w9
   97b64:      	cmp	w8, #0xa
   97b68:      	csinv	w9, w12, wzr, eq
   97b6c:      	mov	x23, x12
   97b70:      	b	0x978a4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
   97b74:      	ldr	w9, [x21, #0x28]
   97b78:      	ldrb	w13, [x21, #0x2c]
   97b7c:      	sub	w13, w9, w13
   97b80:      	str	w13, [x20, #0x24]
   97b84:      	ldrb	w13, [x8, #0x110]
   97b88:      	cmp	w13, #0x1
   97b8c:      	b.ne	0x97bc0 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x434>
   97b90:      	ldrb	w13, [x8, #0x13c]
   97b94:      	cmp	w13, #0x1
   97b98:      	b.ne	0x97bc0 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x434>
   97b9c:      	ldr	w13, [x8, #0x138]
   97ba0:      	cmp	w13, w9
   97ba4:      	b.ne	0x97bc0 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x434>
   97ba8:      	mov	x24, #0x0               ; =0
   97bac:      	ldrb	w12, [x8, #0x120]
   97bb0:      	strb	wzr, [x8, #0x110]
   97bb4:      	mov	w9, #-0x1               ; =-1
   97bb8:      	mov	x23, x9
   97bbc:      	b	0x977c8 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x3c>
   97bc0:      	mov	w10, #0x1               ; =1
   97bc4:      	strb	w10, [x8, #0x110]
   97bc8:      	movi.2d	v0, #0000000000000000
   97bcc:      	str	q0, [x20, #0x7120]
   97bd0:      	str	w12, [x8, #0x130]
   97bd4:      	str	w10, [x8, #0x134]
   97bd8:      	str	w9, [x8, #0x138]
   97bdc:      	strb	wzr, [x8, #0x13c]
   97be0:      	ldr	w27, [x20]
   97be4:      	ldr	w28, [x20, #0x10]
   97be8:      	add	x21, x21, #0x20
   97bec:      	mov	x23, #-0x1              ; =-1
   97bf0:      	mov	x24, #0x0               ; =0
   97bf4:      	b	0x144268 <_vsnprintf+0x144268>
   97bf8:      	ldr	w9, [x21, #0x68]
   97bfc:      	ldrb	w12, [x21, #0x6c]
   97c00:      	sub	w12, w9, w12
   97c04:      	str	w12, [x20, #0x24]
   97c08:      	ldrb	w12, [x8, #0x110]
   97c0c:      	cmp	w12, #0x1
   97c10:      	b.ne	0x97c98 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x50c>
   97c14:      	ldrb	w12, [x8, #0x13c]
   97c18:      	cmp	w12, #0x1
   97c1c:      	b.ne	0x97c98 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x50c>
   97c20:      	ldr	w12, [x8, #0x138]
   97c24:      	cmp	w12, w9
   97c28:      	b.ne	0x97c98 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x50c>
   97c2c:      	mov	x24, #0x0               ; =0
   97c30:      	ldr	w11, [x8, #0x120]
   97c34:      	strb	wzr, [x8, #0x110]
   97c38:      	mov	w9, #-0x1               ; =-1
   97c3c:      	mov	x23, x9
   97c40:      	b	0x97870 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xe4>
   97c44:      	ldr	w9, [x21, #0x68]
   97c48:      	ldrb	w12, [x21, #0x6c]
   97c4c:      	sub	w12, w9, w12
   97c50:      	str	w12, [x20, #0x24]
   97c54:      	ldrb	w12, [x8, #0x110]
   97c58:      	cmp	w12, #0x1
   97c5c:      	b.ne	0x97c90 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x504>
   97c60:      	ldrb	w12, [x8, #0x13c]
   97c64:      	cmp	w12, #0x1
   97c68:      	b.ne	0x97c90 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x504>
   97c6c:      	ldr	w12, [x8, #0x138]
   97c70:      	cmp	w12, w9
   97c74:      	b.ne	0x97c90 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x504>
   97c78:      	mov	x24, #0x0               ; =0
   97c7c:      	ldrh	w11, [x8, #0x120]
   97c80:      	strb	wzr, [x8, #0x110]
   97c84:      	mov	w9, #-0x1               ; =-1
   97c88:      	mov	x23, x9
   97c8c:      	b	0x978a4 <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
   97c90:      	mov	w10, #0x2               ; =2
   97c94:      	b	0x97c9c <__ZN8fiberish77SuperOpcode_4_039_OpMov_EvGv_Esi__OpMovzx_Byte__OpMov_Load_Ecx__OpXor_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x510>
   97c98:      	mov	w10, #0x4               ; =4
   97c9c:      	mov	w12, #0x1               ; =1
   97ca0:      	strb	w12, [x8, #0x110]
   97ca4:      	movi.2d	v0, #0000000000000000
   97ca8:      	str	q0, [x20, #0x7120]
   97cac:      	str	w11, [x8, #0x130]
   97cb0:      	str	w10, [x8, #0x134]
   97cb4:      	str	w9, [x8, #0x138]
   97cb8:      	strb	wzr, [x8, #0x13c]
   97cbc:      	ldr	w27, [x20]
   97cc0:      	ldr	w28, [x20, #0x10]
   97cc4:      	add	x21, x21, #0x60
   97cc8:      	mov	x23, #-0x1              ; =-1
   97ccc:      	mov	x24, #0x0               ; =0
   97cd0:      	b	0x144268 <_vsnprintf+0x144268>
