0000000000203af4 T fiberish::OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32(fiberish::EmuState*, fiberish::DecodedOp*, long long, unsigned long long, unsigned long long, unsigned int, unsigned long long, unsigned int, unsigned int)

combined-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000203af4 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>:
  203af4:      	mov	x8, x27
  203af8:      	mov	w9, #0xd120             ; =53536
  203afc:      	add	x9, x20, x9
  203b00:      	ands	w27, w27, #0x7fff
  203b04:      	mov	w10, #0x40              ; =64
  203b08:      	csel	x11, x10, xzr, eq
  203b0c:      	mov	x10, #-0x8d6            ; =-2262
  203b10:      	movk	x10, #0xff, lsl #32
  203b14:      	and	x10, x26, x10
  203b18:      	and	w8, w8, #0xff
  203b1c:      	orr	x11, x11, x8, lsl #40
  203b20:      	ldr	w8, [x20, #0x14]
  203b24:      	add	w8, w8, w27, lsl #1
  203b28:      	ldr	w12, [x20, #0x40]
  203b2c:      	add	w13, w8, w12
  203b30:      	mov	w8, #0xfff              ; =4095
  203b34:      	bics	wzr, w8, w13
  203b38:      	b.eq	0x203be4 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xf0>
  203b3c:      	and	w8, w13, #0xfffff000
  203b40:      	cmp	w8, w23
  203b44:      	b.ne	0x203bb4 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xc0>
  203b48:      	ldrh	w27, [x24, w13, uxtw]
  203b4c:      	add	w12, w12, w28
  203b50:      	and	w8, w12, #0xfff
  203b54:      	cmp	w8, #0xffc
  203b58:      	b.hi	0x203d18 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x224>
  203b5c:      	and	w8, w12, #0xfffff000
  203b60:      	cmp	w8, w23
  203b64:      	b.ne	0x203c28 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x134>
  203b68:      	ldr	w10, [x24, w12, uxtw]
  203b6c:      	subs	w8, w10, w27
  203b70:      	b.hs	0x203c4c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x158>
  203b74:      	eor	w9, w10, w27
  203b78:      	lsr	w10, w8, #31
  203b7c:      	eor	w9, w9, w8
  203b80:      	and	w9, w9, #0x10
  203b84:      	mov	w11, #-0x8d6            ; =-2262
  203b88:      	and	w11, w26, w11
  203b8c:      	orr	w10, w11, w10, lsl #7
  203b90:      	orr	w9, w10, w9
  203b94:      	orr	w9, w9, #0x1
  203b98:      	and	x10, x26, #0xffffffff00000000
  203b9c:      	and	x10, x10, #0xffff00ffffffffff
  203ba0:      	orr	x9, x10, x9
  203ba4:      	and	w8, w8, #0xff
  203ba8:      	orr	x26, x9, x8, lsl #40
  203bac:      	ldr	x0, [x21, #0x50]!
  203bb0:      	br	x0
  203bb4:      	ubfx	w14, w13, #12, #10
  203bb8:      	add	x14, x20, w14, uxtw #4
  203bbc:      	ldr	x23, [x14, #0x1a0]
  203bc0:      	cmp	w8, w23
  203bc4:      	b.ne	0x203be4 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xf0>
  203bc8:      	ldr	x24, [x14, #0x1a8]
  203bcc:      	ldrh	w27, [x24, w13, uxtw]
  203bd0:      	add	w12, w12, w28
  203bd4:      	and	w8, w12, #0xfff
  203bd8:      	cmp	w8, #0xffc
  203bdc:      	b.ls	0x203b5c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x68>
  203be0:      	b	0x203d18 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x224>
  203be4:      	add	x8, x21, #0x10
  203be8:      	ldrb	w14, [x9]
  203bec:      	cmp	w14, #0x1
  203bf0:      	b.ne	0x203c0c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
  203bf4:      	ldrb	w14, [x9, #0x2d]
  203bf8:      	cmp	w14, #0x1
  203bfc:      	b.ne	0x203c0c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
  203c00:      	ldr	x14, [x9, #0x20]
  203c04:      	cmp	x14, x8
  203c08:      	b.eq	0x203cf8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x204>
  203c0c:      	mov	w12, #0x1               ; =1
  203c10:      	strb	w12, [x9]
  203c14:      	movi.2d	v0, #0000000000000000
  203c18:      	str	q0, [x20, #0xd130]
  203c1c:      	str	w13, [x9, #0x28]
  203c20:      	mov	w12, #0x2               ; =2
  203c24:      	b	0x203d58 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x264>
  203c28:      	ubfx	w13, w12, #12, #10
  203c2c:      	add	x13, x20, w13, uxtw #4
  203c30:      	ldr	x23, [x13, #0x1a0]
  203c34:      	cmp	w8, w23
  203c38:      	b.ne	0x203d18 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x224>
  203c3c:      	ldr	x24, [x13, #0x1a8]
  203c40:      	ldr	w10, [x24, w12, uxtw]
  203c44:      	subs	w8, w10, w27
  203c48:      	b.lo	0x203b74 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x80>
  203c4c:      	ldr	x25, [x21, #0x38]
  203c50:      	mov	w11, #0x40              ; =64
  203c54:      	csel	w11, w11, wzr, eq
  203c58:      	eor	w12, w10, w27
  203c5c:      	bic	w10, w10, w8
  203c60:      	lsr	w13, w8, #31
  203c64:      	lsr	w10, w10, #31
  203c68:      	eor	w12, w12, w8
  203c6c:      	and	w12, w12, #0x10
  203c70:      	mov	w14, #-0x8d6            ; =-2262
  203c74:      	and	w14, w26, w14
  203c78:      	orr	w11, w11, w14
  203c7c:      	orr	w11, w11, w13, lsl #7
  203c80:      	orr	w11, w11, w12
  203c84:      	orr	w10, w11, w10, lsl #11
  203c88:      	and	x11, x26, #0xffffffff00000000
  203c8c:      	and	x11, x11, #0xffff00ffffffffff
  203c90:      	orr	x10, x11, x10
  203c94:      	and	w8, w8, #0xff
  203c98:      	orr	x26, x10, x8, lsl #40
  203c9c:      	strb	wzr, [x9]
  203ca0:      	cmp	x22, #0x1
  203ca4:      	b.lt	0x203cd8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1e4>
  203ca8:      	ldr	x8, [x21, #0x48]
  203cac:      	ldp	x9, x0, [x8]
  203cb0:      	cmp	w25, w9
  203cb4:      	b.ne	0x203cf0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1fc>
  203cb8:      	tbz	x9, #0x28, 0x203cf0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1fc>
  203cbc:      	mov	w10, #0xbe00            ; =48640
  203cc0:      	str	x8, [x20, x10]
  203cc4:      	lsr	x9, x9, #32
  203cc8:      	sub	x22, x22, w9, uxtb
  203ccc:      	add	x21, x8, #0x30
  203cd0:      	mov	w25, #-0x1              ; =-1
  203cd4:      	br	x0
  203cd8:      	str	w25, [x20, #0x24]
  203cdc:      	str	w27, [x20]
  203ce0:      	str	w28, [x20, #0x10]
  203ce4:      	str	x26, [x20, #0x28]
  203ce8:      	mov	x0, x22
  203cec:      	ret
  203cf0:      	add	x21, x21, #0x30
  203cf4:      	b	0x5a1c8 <__ZN8fiberish33ResolveBranchTargetSlowDoubleSlotEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>
  203cf8:      	mov	x24, #0x0               ; =0
  203cfc:      	ldrh	w27, [x9, #0x10]
  203d00:      	strb	wzr, [x9]
  203d04:      	mov	x23, #-0x1              ; =-1
  203d08:      	add	w12, w12, w28
  203d0c:      	and	w8, w12, #0xfff
  203d10:      	cmp	w8, #0xffc
  203d14:      	b.ls	0x203b5c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x68>
  203d18:      	add	x8, x21, #0x20
  203d1c:      	ldrb	w13, [x9]
  203d20:      	cmp	w13, #0x1
  203d24:      	b.ne	0x203d40 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x24c>
  203d28:      	ldrb	w13, [x9, #0x2d]
  203d2c:      	cmp	w13, #0x1
  203d30:      	b.ne	0x203d40 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x24c>
  203d34:      	ldr	x13, [x9, #0x20]
  203d38:      	cmp	x13, x8
  203d3c:      	b.eq	0x203d74 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x280>
  203d40:      	mov	w13, #0x1               ; =1
  203d44:      	strb	w13, [x9]
  203d48:      	movi.2d	v0, #0000000000000000
  203d4c:      	str	q0, [x20, #0xd130]
  203d50:      	str	w12, [x9, #0x28]
  203d54:      	mov	w12, #0x4               ; =4
  203d58:      	strh	w12, [x9, #0x2c]
  203d5c:      	str	x8, [x9, #0x20]
  203d60:      	orr	x26, x11, x10
  203d64:      	mov	x21, x8
  203d68:      	mov	x23, #-0x1              ; =-1
  203d6c:      	mov	x24, #0x0               ; =0
  203d70:      	b	0x54ca08 <_vsnprintf+0x54ca08>
  203d74:      	mov	x24, #0x0               ; =0
  203d78:      	ldr	w10, [x9, #0x10]
  203d7c:      	strb	wzr, [x9]
  203d80:      	mov	x23, #-0x1              ; =-1
  203d84:      	subs	w8, w10, w27
  203d88:      	b.hs	0x203c4c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x158>
  203d8c:      	b	0x203b74 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x80>
