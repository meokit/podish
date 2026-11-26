00000000001fa950 T fiberish::OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32(fiberish::EmuState*, fiberish::DecodedOp*, long long, unsigned long long, unsigned long long, unsigned int, unsigned long long, unsigned int, unsigned int)

i386-flags-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000001fa950 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>:
  1fa950:      	mov	x8, x27
  1fa954:      	mov	w9, #0xd120             ; =53536
  1fa958:      	add	x9, x20, x9
  1fa95c:      	ands	w27, w27, #0x7fff
  1fa960:      	mov	w10, #0x40              ; =64
  1fa964:      	csel	x11, x10, xzr, eq
  1fa968:      	mov	x10, #-0x8d6            ; =-2262
  1fa96c:      	movk	x10, #0xff, lsl #32
  1fa970:      	and	x10, x26, x10
  1fa974:      	and	w8, w8, #0xff
  1fa978:      	orr	x11, x11, x8, lsl #40
  1fa97c:      	ldr	w8, [x20, #0x14]
  1fa980:      	add	w8, w8, w27, lsl #1
  1fa984:      	ldr	w12, [x20, #0x40]
  1fa988:      	add	w13, w8, w12
  1fa98c:      	mov	w8, #0xfff              ; =4095
  1fa990:      	bics	wzr, w8, w13
  1fa994:      	b.eq	0x1faa40 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xf0>
  1fa998:      	and	w8, w13, #0xfffff000
  1fa99c:      	cmp	w8, w23
  1fa9a0:      	b.ne	0x1faa10 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xc0>
  1fa9a4:      	ldrh	w27, [x24, w13, uxtw]
  1fa9a8:      	add	w12, w12, w28
  1fa9ac:      	and	w8, w12, #0xfff
  1fa9b0:      	cmp	w8, #0xffc
  1fa9b4:      	b.hi	0x1fab74 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x224>
  1fa9b8:      	and	w8, w12, #0xfffff000
  1fa9bc:      	cmp	w8, w23
  1fa9c0:      	b.ne	0x1faa84 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x134>
  1fa9c4:      	ldr	w10, [x24, w12, uxtw]
  1fa9c8:      	subs	w8, w10, w27
  1fa9cc:      	b.hs	0x1faaa8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x158>
  1fa9d0:      	eor	w9, w10, w27
  1fa9d4:      	lsr	w10, w8, #31
  1fa9d8:      	eor	w9, w9, w8
  1fa9dc:      	and	w9, w9, #0x10
  1fa9e0:      	mov	w11, #-0x8d6            ; =-2262
  1fa9e4:      	and	w11, w26, w11
  1fa9e8:      	orr	w10, w11, w10, lsl #7
  1fa9ec:      	orr	w9, w10, w9
  1fa9f0:      	orr	w9, w9, #0x1
  1fa9f4:      	and	x10, x26, #0xffffffff00000000
  1fa9f8:      	and	x10, x10, #0xffff00ffffffffff
  1fa9fc:      	orr	x9, x10, x9
  1faa00:      	and	w8, w8, #0xff
  1faa04:      	orr	x26, x9, x8, lsl #40
  1faa08:      	ldr	x0, [x21, #0x50]!
  1faa0c:      	br	x0
  1faa10:      	ubfx	w14, w13, #12, #10
  1faa14:      	add	x14, x20, w14, uxtw #4
  1faa18:      	ldr	x23, [x14, #0x1a0]
  1faa1c:      	cmp	w8, w23
  1faa20:      	b.ne	0x1faa40 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0xf0>
  1faa24:      	ldr	x24, [x14, #0x1a8]
  1faa28:      	ldrh	w27, [x24, w13, uxtw]
  1faa2c:      	add	w12, w12, w28
  1faa30:      	and	w8, w12, #0xfff
  1faa34:      	cmp	w8, #0xffc
  1faa38:      	b.ls	0x1fa9b8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x68>
  1faa3c:      	b	0x1fab74 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x224>
  1faa40:      	add	x8, x21, #0x10
  1faa44:      	ldrb	w14, [x9]
  1faa48:      	cmp	w14, #0x1
  1faa4c:      	b.ne	0x1faa68 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
  1faa50:      	ldrb	w14, [x9, #0x2d]
  1faa54:      	cmp	w14, #0x1
  1faa58:      	b.ne	0x1faa68 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
  1faa5c:      	ldr	x14, [x9, #0x20]
  1faa60:      	cmp	x14, x8
  1faa64:      	b.eq	0x1fab54 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x204>
  1faa68:      	mov	w12, #0x1               ; =1
  1faa6c:      	strb	w12, [x9]
  1faa70:      	movi.2d	v0, #0000000000000000
  1faa74:      	str	q0, [x20, #0xd130]
  1faa78:      	str	w13, [x9, #0x28]
  1faa7c:      	mov	w12, #0x2               ; =2
  1faa80:      	b	0x1fabb4 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x264>
  1faa84:      	ubfx	w13, w12, #12, #10
  1faa88:      	add	x13, x20, w13, uxtw #4
  1faa8c:      	ldr	x23, [x13, #0x1a0]
  1faa90:      	cmp	w8, w23
  1faa94:      	b.ne	0x1fab74 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x224>
  1faa98:      	ldr	x24, [x13, #0x1a8]
  1faa9c:      	ldr	w10, [x24, w12, uxtw]
  1faaa0:      	subs	w8, w10, w27
  1faaa4:      	b.lo	0x1fa9d0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x80>
  1faaa8:      	ldr	x25, [x21, #0x38]
  1faaac:      	mov	w11, #0x40              ; =64
  1faab0:      	csel	w11, w11, wzr, eq
  1faab4:      	eor	w12, w10, w27
  1faab8:      	bic	w10, w10, w8
  1faabc:      	lsr	w13, w8, #31
  1faac0:      	lsr	w10, w10, #31
  1faac4:      	eor	w12, w12, w8
  1faac8:      	and	w12, w12, #0x10
  1faacc:      	mov	w14, #-0x8d6            ; =-2262
  1faad0:      	and	w14, w26, w14
  1faad4:      	orr	w11, w11, w14
  1faad8:      	orr	w11, w11, w13, lsl #7
  1faadc:      	orr	w11, w11, w12
  1faae0:      	orr	w10, w11, w10, lsl #11
  1faae4:      	and	x11, x26, #0xffffffff00000000
  1faae8:      	and	x11, x11, #0xffff00ffffffffff
  1faaec:      	orr	x10, x11, x10
  1faaf0:      	and	w8, w8, #0xff
  1faaf4:      	orr	x26, x10, x8, lsl #40
  1faaf8:      	strb	wzr, [x9]
  1faafc:      	cmp	x22, #0x1
  1fab00:      	b.lt	0x1fab34 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1e4>
  1fab04:      	ldr	x8, [x21, #0x48]
  1fab08:      	ldp	x9, x0, [x8]
  1fab0c:      	cmp	w25, w9
  1fab10:      	b.ne	0x1fab4c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1fc>
  1fab14:      	tbz	x9, #0x28, 0x1fab4c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1fc>
  1fab18:      	mov	w10, #0xbe00            ; =48640
  1fab1c:      	str	x8, [x20, x10]
  1fab20:      	lsr	x9, x9, #32
  1fab24:      	sub	x22, x22, w9, uxtb
  1fab28:      	add	x21, x8, #0x30
  1fab2c:      	mov	w25, #-0x1              ; =-1
  1fab30:      	br	x0
  1fab34:      	str	w25, [x20, #0x24]
  1fab38:      	str	w27, [x20]
  1fab3c:      	str	w28, [x20, #0x10]
  1fab40:      	str	x26, [x20, #0x28]
  1fab44:      	mov	x0, x22
  1fab48:      	ret
  1fab4c:      	add	x21, x21, #0x30
  1fab50:      	b	0x51024 <__ZN8fiberish33ResolveBranchTargetSlowDoubleSlotEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>
  1fab54:      	mov	x24, #0x0               ; =0
  1fab58:      	ldrh	w27, [x9, #0x10]
  1fab5c:      	strb	wzr, [x9]
  1fab60:      	mov	x23, #-0x1              ; =-1
  1fab64:      	add	w12, w12, w28
  1fab68:      	and	w8, w12, #0xfff
  1fab6c:      	cmp	w8, #0xffc
  1fab70:      	b.ls	0x1fa9b8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x68>
  1fab74:      	add	x8, x21, #0x20
  1fab78:      	ldrb	w13, [x9]
  1fab7c:      	cmp	w13, #0x1
  1fab80:      	b.ne	0x1fab9c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x24c>
  1fab84:      	ldrb	w13, [x9, #0x2d]
  1fab88:      	cmp	w13, #0x1
  1fab8c:      	b.ne	0x1fab9c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x24c>
  1fab90:      	ldr	x13, [x9, #0x20]
  1fab94:      	cmp	x13, x8
  1fab98:      	b.eq	0x1fabd0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x280>
  1fab9c:      	mov	w13, #0x1               ; =1
  1faba0:      	strb	w13, [x9]
  1faba4:      	movi.2d	v0, #0000000000000000
  1faba8:      	str	q0, [x20, #0xd130]
  1fabac:      	str	w12, [x9, #0x28]
  1fabb0:      	mov	w12, #0x4               ; =4
  1fabb4:      	strh	w12, [x9, #0x2c]
  1fabb8:      	str	x8, [x9, #0x20]
  1fabbc:      	orr	x26, x11, x10
  1fabc0:      	mov	x21, x8
  1fabc4:      	mov	x23, #-0x1              ; =-1
  1fabc8:      	mov	x24, #0x0               ; =0
  1fabcc:      	b	0x545a50 <_vsnprintf+0x545a50>
  1fabd0:      	mov	x24, #0x0               ; =0
  1fabd4:      	ldr	w10, [x9, #0x10]
  1fabd8:      	strb	wzr, [x9]
  1fabdc:      	mov	x23, #-0x1              ; =-1
  1fabe0:      	subs	w8, w10, w27
  1fabe4:      	b.hs	0x1faaa8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x158>
  1fabe8:      	b	0x1fa9d0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x80>
