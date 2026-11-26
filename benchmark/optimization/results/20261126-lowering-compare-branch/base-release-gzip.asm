00000000001fa908 T fiberish::OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32(fiberish::EmuState*, fiberish::DecodedOp*, long long, unsigned long long, unsigned long long, unsigned int, unsigned long long, unsigned int, unsigned int)

base-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000001fa908 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>:
  1fa908:      	mov	x8, x27
  1fa90c:      	mov	w9, #0xd120             ; =53536
  1fa910:      	add	x9, x20, x9
  1fa914:      	ands	w27, w27, #0x7fff
  1fa918:      	mov	w10, #0x40              ; =64
  1fa91c:      	csel	x11, x10, xzr, eq
  1fa920:      	mov	x10, #-0x8d6            ; =-2262
  1fa924:      	movk	x10, #0xff, lsl #32
  1fa928:      	and	x10, x26, x10
  1fa92c:      	and	w8, w8, #0xff
  1fa930:      	orr	x11, x11, x8, lsl #40
  1fa934:      	ldr	w8, [x20, #0x14]
  1fa938:      	add	w8, w8, w27, lsl #1
  1fa93c:      	ldr	w12, [x20, #0x40]
  1fa940:      	add	w13, w8, w12
  1fa944:      	mov	w8, #0xfff              ; =4095
  1fa948:      	bics	wzr, w8, w13
  1fa94c:      	b.eq	0x1faa68 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x160>
  1fa950:      	and	w8, w13, #0xfffff000
  1fa954:      	cmp	w8, w23
  1fa958:      	b.ne	0x1faa38 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x130>
  1fa95c:      	ldrh	w27, [x24, w13, uxtw]
  1fa960:      	add	w12, w12, w28
  1fa964:      	and	w8, w12, #0xfff
  1fa968:      	cmp	w8, #0xffc
  1fa96c:      	b.hi	0x1faaf0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1e8>
  1fa970:      	and	w8, w12, #0xfffff000
  1fa974:      	cmp	w8, w23
  1fa978:      	b.ne	0x1faaac <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1a4>
  1fa97c:      	ldr	w8, [x24, w12, uxtw]
  1fa980:      	mov	w10, #0x40              ; =64
  1fa984:      	subs	w11, w8, w27
  1fa988:      	eor	w12, w11, w8
  1fa98c:      	csel	w10, w10, wzr, eq
  1fa990:      	lsr	w13, w11, #31
  1fa994:      	cinc	w10, w10, lo
  1fa998:      	orr	w10, w10, w13, lsl #7
  1fa99c:      	and	w8, w8, w12
  1fa9a0:      	lsr	w8, w8, #31
  1fa9a4:      	orr	w8, w10, w8, lsl #11
  1fa9a8:      	eor	w10, w12, w27
  1fa9ac:      	and	w10, w10, #0x10
  1fa9b0:      	mov	w12, #-0x8d6            ; =-2262
  1fa9b4:      	and	w12, w26, w12
  1fa9b8:      	orr	w10, w10, w12
  1fa9bc:      	orr	w8, w10, w8
  1fa9c0:      	and	x10, x26, #0xffffffff00000000
  1fa9c4:      	and	x10, x10, #0xffff00ffffffffff
  1fa9c8:      	and	w11, w11, #0xff
  1fa9cc:      	orr	x10, x10, x11, lsl #40
  1fa9d0:      	orr	x26, x10, x8
  1fa9d4:      	tbnz	w8, #0x0, 0x1faa18 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x110>
  1fa9d8:      	ldr	x25, [x21, #0x38]
  1fa9dc:      	strb	wzr, [x9]
  1fa9e0:      	cmp	x22, #0x1
  1fa9e4:      	b.lt	0x1faa20 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x118>
  1fa9e8:      	ldr	x8, [x21, #0x48]
  1fa9ec:      	ldp	x9, x0, [x8]
  1fa9f0:      	cmp	w25, w9
  1fa9f4:      	b.ne	0x1faac8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1c0>
  1fa9f8:      	tbz	x9, #0x28, 0x1faac8 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1c0>
  1fa9fc:      	mov	w10, #0xbe00            ; =48640
  1faa00:      	str	x8, [x20, x10]
  1faa04:      	lsr	x9, x9, #32
  1faa08:      	sub	x22, x22, w9, uxtb
  1faa0c:      	add	x21, x8, #0x30
  1faa10:      	mov	w25, #-0x1              ; =-1
  1faa14:      	br	x0
  1faa18:      	ldr	x0, [x21, #0x50]!
  1faa1c:      	br	x0
  1faa20:      	str	w25, [x20, #0x24]
  1faa24:      	str	w27, [x20]
  1faa28:      	str	w28, [x20, #0x10]
  1faa2c:      	str	x26, [x20, #0x28]
  1faa30:      	mov	x0, x22
  1faa34:      	ret
  1faa38:      	ubfx	w14, w13, #12, #10
  1faa3c:      	add	x14, x20, w14, uxtw #4
  1faa40:      	ldr	x23, [x14, #0x1a0]
  1faa44:      	cmp	w8, w23
  1faa48:      	b.ne	0x1faa68 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x160>
  1faa4c:      	ldr	x24, [x14, #0x1a8]
  1faa50:      	ldrh	w27, [x24, w13, uxtw]
  1faa54:      	add	w12, w12, w28
  1faa58:      	and	w8, w12, #0xfff
  1faa5c:      	cmp	w8, #0xffc
  1faa60:      	b.ls	0x1fa970 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x68>
  1faa64:      	b	0x1faaf0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1e8>
  1faa68:      	add	x8, x21, #0x10
  1faa6c:      	ldrb	w14, [x9]
  1faa70:      	cmp	w14, #0x1
  1faa74:      	b.ne	0x1faa90 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x188>
  1faa78:      	ldrb	w14, [x9, #0x2d]
  1faa7c:      	cmp	w14, #0x1
  1faa80:      	b.ne	0x1faa90 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x188>
  1faa84:      	ldr	x14, [x9, #0x20]
  1faa88:      	cmp	x14, x8
  1faa8c:      	b.eq	0x1faad0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1c8>
  1faa90:      	mov	w12, #0x1               ; =1
  1faa94:      	strb	w12, [x9]
  1faa98:      	movi.2d	v0, #0000000000000000
  1faa9c:      	str	q0, [x20, #0xd130]
  1faaa0:      	str	w13, [x9, #0x28]
  1faaa4:      	mov	w12, #0x2               ; =2
  1faaa8:      	b	0x1fab30 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x228>
  1faaac:      	ubfx	w13, w12, #12, #10
  1faab0:      	add	x13, x20, w13, uxtw #4
  1faab4:      	ldr	x23, [x13, #0x1a0]
  1faab8:      	cmp	w8, w23
  1faabc:      	b.ne	0x1faaf0 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x1e8>
  1faac0:      	ldr	x24, [x13, #0x1a8]
  1faac4:      	b	0x1fa97c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x74>
  1faac8:      	add	x21, x21, #0x30
  1faacc:      	b	0x51024 <__ZN8fiberish33ResolveBranchTargetSlowDoubleSlotEPNS_8EmuStateEPNS_9DecodedOpExyyjyjj>
  1faad0:      	mov	x24, #0x0               ; =0
  1faad4:      	ldrh	w27, [x9, #0x10]
  1faad8:      	strb	wzr, [x9]
  1faadc:      	mov	x23, #-0x1              ; =-1
  1faae0:      	add	w12, w12, w28
  1faae4:      	and	w8, w12, #0xfff
  1faae8:      	cmp	w8, #0xffc
  1faaec:      	b.ls	0x1fa970 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x68>
  1faaf0:      	add	x8, x21, #0x20
  1faaf4:      	ldrb	w13, [x9]
  1faaf8:      	cmp	w13, #0x1
  1faafc:      	b.ne	0x1fab18 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x210>
  1fab00:      	ldrb	w13, [x9, #0x2d]
  1fab04:      	cmp	w13, #0x1
  1fab08:      	b.ne	0x1fab18 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x210>
  1fab0c:      	ldr	x13, [x9, #0x20]
  1fab10:      	cmp	x13, x8
  1fab14:      	b.eq	0x1fab4c <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x244>
  1fab18:      	mov	w13, #0x1               ; =1
  1fab1c:      	strb	w13, [x9]
  1fab20:      	movi.2d	v0, #0000000000000000
  1fab24:      	str	q0, [x20, #0xd130]
  1fab28:      	str	w12, [x9, #0x28]
  1fab2c:      	mov	w12, #0x4               ; =4
  1fab30:      	strh	w12, [x9, #0x2c]
  1fab34:      	str	x8, [x9, #0x20]
  1fab38:      	orr	x26, x11, x10
  1fab3c:      	mov	x21, x8
  1fab40:      	mov	x23, #-0x1              ; =-1
  1fab44:      	mov	x24, #0x0               ; =0
  1fab48:      	b	0x544724 <_vsnprintf+0x544724>
  1fab4c:      	mov	x24, #0x0               ; =0
  1fab50:      	ldr	w8, [x9, #0x10]
  1fab54:      	strb	wzr, [x9]
  1fab58:      	mov	x23, #-0x1              ; =-1
  1fab5c:      	b	0x1fa980 <__ZN8fiberish89OperandClone_032_SuperOpcode_4_029_OpAnd_EaxImm__OpMovzx_Word__OpCmp_EvGv__OpJcc_AE_Rel32EPNS_8EmuStateEPNS_9DecodedOpExyyjyjj+0x78>
