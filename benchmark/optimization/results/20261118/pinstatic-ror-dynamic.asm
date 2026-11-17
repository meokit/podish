
/Users/jiangyiheng/repos/x86emu/artifacts/fusion-specialization-pin-20261118/pinstatic-build/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000066c1c <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op20OpRotateRegImmediateIjLb1ELb1EEENS_9LogicFlowEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_20DynamicOperandPolicyEEExS5_S7_xyyjyjj>:
   66c1c:      	ldrb	w8, [x21, #0xf]
   66c20:      	tbnz	w8, #0x2, 0x66c48 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op20OpRotateRegImmediateIjLb1ELb1EEENS_9LogicFlowEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_20DynamicOperandPolicyEEExS5_S7_xyyjyjj+0x2c>
   66c24:      	ldrb	w8, [x21, #0xd]
   66c28:      	and	x8, x8, #0x7
   66c2c:      	add	x8, x20, x8, lsl #2
   66c30:      	ldr	w9, [x8]
   66c34:      	ldr	x0, [x21, #0x20]!
   66c38:      	str	w9, [x8]
   66c3c:      	ldr	w27, [x20]
   66c40:      	ldr	w28, [x20, #0x10]
   66c44:      	br	x0
   66c48:      	ldr	w11, [x21, #0x10]
   66c4c:      	ldrb	w8, [x21, #0xd]
   66c50:      	and	x8, x8, #0x7
   66c54:      	add	x8, x20, x8, lsl #2
   66c58:      	ldr	w9, [x8]
   66c5c:      	ands	w10, w11, #0x1f
   66c60:      	b.eq	0x66c34 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op20OpRotateRegImmediateIjLb1ELb1EEENS_9LogicFlowEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_20DynamicOperandPolicyEEExS5_S7_xyyjyjj+0x18>
   66c64:      	ror	w9, w9, w11
   66c68:      	and	x11, x26, #0xfffffffffffffffe
   66c6c:      	lsr	w12, w9, #31
   66c70:      	orr	x11, x11, x12
   66c74:      	ubfx	w13, w9, #30, #1
   66c78:      	cmp	w12, w13
   66c7c:      	and	x12, x11, #0xfffffffffffff7ff
   66c80:      	orr	x13, x11, #0x800
   66c84:      	csel	x12, x12, x13, eq
   66c88:      	cmp	w10, #0x1
   66c8c:      	csel	x26, x11, x12, ne
   66c90:      	ldr	x0, [x21, #0x20]!
   66c94:      	str	w9, [x8]
   66c98:      	ldr	w27, [x20]
   66c9c:      	ldr	w28, [x20, #0x10]
   66ca0:      	br	x0
