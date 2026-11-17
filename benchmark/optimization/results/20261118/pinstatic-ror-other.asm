
/Users/jiangyiheng/repos/x86emu/artifacts/fusion-specialization-pin-20261118/pinstatic-build/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000001349a0 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op20OpRotateRegImmediateIjLb1ELb1EEENS_9LogicFlowEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_21RegisterOperandPolicyILNS_12PinWriteRoleE1ELNS_13PinWriteClassEn1ELb0EEEEExS5_S7_xyyjyjj>:
  1349a0:      	ldrb	w8, [x21, #0xd]
  1349a4:      	ldrb	w9, [x21, #0xf]
  1349a8:      	tbnz	w9, #0x2, 0x1349c4 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op20OpRotateRegImmediateIjLb1ELb1EEENS_9LogicFlowEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_21RegisterOperandPolicyILNS_12PinWriteRoleE1ELNS_13PinWriteClassEn1ELb0EEEEExS5_S7_xyyjyjj+0x24>
  1349ac:      	and	x8, x8, #0x7
  1349b0:      	add	x8, x20, x8, lsl #2
  1349b4:      	ldr	w9, [x8]
  1349b8:      	str	w9, [x8]
  1349bc:      	ldr	x0, [x21, #0x20]!
  1349c0:      	br	x0
  1349c4:      	ldr	w11, [x21, #0x10]
  1349c8:      	and	x8, x8, #0x7
  1349cc:      	add	x8, x20, x8, lsl #2
  1349d0:      	ldr	w9, [x8]
  1349d4:      	ands	w10, w11, #0x1f
  1349d8:      	b.eq	0x1349b8 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op20OpRotateRegImmediateIjLb1ELb1EEENS_9LogicFlowEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_21RegisterOperandPolicyILNS_12PinWriteRoleE1ELNS_13PinWriteClassEn1ELb0EEEEExS5_S7_xyyjyjj+0x18>
  1349dc:      	ror	w9, w9, w11
  1349e0:      	and	x11, x26, #0xfffffffffffffffe
  1349e4:      	lsr	w12, w9, #31
  1349e8:      	orr	x11, x11, x12
  1349ec:      	ubfx	w13, w9, #30, #1
  1349f0:      	cmp	w12, w13
  1349f4:      	and	x12, x11, #0xfffffffffffff7ff
  1349f8:      	orr	x13, x11, #0x800
  1349fc:      	csel	x12, x12, x13, eq
  134a00:      	cmp	w10, #0x1
  134a04:      	csel	x26, x11, x12, ne
  134a08:      	str	w9, [x8]
  134a0c:      	ldr	x0, [x21, #0x20]!
  134a10:      	br	x0
