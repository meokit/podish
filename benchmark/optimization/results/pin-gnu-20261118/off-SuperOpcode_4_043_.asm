
/Users/jiangyiheng/repos/x86emu/artifacts/pin-gnu-20261118/off-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000097d08 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy>:
   97d08:      	sub	sp, sp, #0x20
   97d0c:      	stp	x29, x30, [sp, #0x10]
   97d10:      	add	x9, x20, #0x7, lsl #12  ; =0x7000
   97d14:      	ldp	w8, w10, [x21, #0x14]
   97d18:      	and	x11, x8, #0x3f
   97d1c:      	ldr	w11, [x11, x20]
   97d20:      	ubfx	x12, x8, #6, #6
   97d24:      	ldr	w12, [x12, x20]
   97d28:      	ubfx	w13, w8, #12, #2
   97d2c:      	lsl	w12, w12, w13
   97d30:      	add	w10, w11, w10
   97d34:      	add	w10, w10, w12
   97d38:      	ldrsb	w11, [x21, #0xe]
   97d3c:      	and	w12, w10, #0xffff
   97d40:      	cmp	w11, #0x0
   97d44:      	csel	w10, w12, w10, lt
   97d48:      	ubfx	x8, x8, #17, #3
   97d4c:      	add	x19, x20, #0x34
   97d50:      	ldr	w8, [x19, x8, lsl #2]
   97d54:      	add	w10, w10, w8
   97d58:      	add	w8, w10, #0x3
   97d5c:      	eor	w8, w8, w10
   97d60:      	and	w11, w10, #0xfffff000
   97d64:      	and	w8, w8, #0xfffff000
   97d68:      	eor	w12, w11, w23
   97d6c:      	orr	w12, w8, w12
   97d70:      	cbnz	w12, 0x97ee4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x1dc>
   97d74:      	lsr	x8, x23, #32
   97d78:      	ldr	w10, [x24, w10, uxtw]
   97d7c:      	str	w10, [x20, #0x8]
   97d80:      	ldp	w10, w11, [x21, #0x34]
   97d84:      	ubfx	x12, x10, #6, #6
   97d88:      	ldr	w12, [x12, x20]
   97d8c:      	ubfx	w13, w10, #12, #2
   97d90:      	lsl	w12, w12, w13
   97d94:      	and	x13, x10, #0x3f
   97d98:      	ldr	w13, [x13, x20]
   97d9c:      	add	w11, w13, w11
   97da0:      	add	w11, w11, w12
   97da4:      	ldrsb	w12, [x21, #0x2e]
   97da8:      	and	w13, w11, #0xffff
   97dac:      	cmp	w12, #0x0
   97db0:      	csel	w11, w13, w11, lt
   97db4:      	ubfx	x10, x10, #17, #3
   97db8:      	ldr	w10, [x19, x10, lsl #2]
   97dbc:      	add	w10, w11, w10
   97dc0:      	add	w11, w10, #0x3
   97dc4:      	eor	w12, w11, w10
   97dc8:      	and	w11, w10, #0xfffff000
   97dcc:      	and	w12, w12, #0xfffff000
   97dd0:      	eor	w13, w11, w23
   97dd4:      	orr	w13, w12, w13
   97dd8:      	cbnz	w13, 0x97f24 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x21c>
   97ddc:      	ldr	w10, [x24, w10, uxtw]
   97de0:      	mov	w9, #0x6e40             ; =28224
   97de4:      	add	x27, x20, x9
   97de8:      	str	w10, [x20]
   97dec:      	ldrb	w9, [x21, #0x4d]
   97df0:      	cmp	w9, #0xc0
   97df4:      	b.lo	0x97f64 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x25c>
   97df8:      	and	x10, x9, #0x7
   97dfc:      	add	x10, x20, x10, lsl #2
   97e00:      	ldr	w10, [x10]
   97e04:      	adds	w11, w10, #0x1
   97e08:      	mov	x12, #-0x8d6            ; =-2262
   97e0c:      	and	x12, x26, x12
   97e10:      	orr	x13, x12, #0x80
   97e14:      	cmp	w11, #0x0
   97e18:      	csel	x11, x13, x12, lt
   97e1c:      	adds	w2, w10, #0x1
   97e20:      	orr	x12, x12, #0x40
   97e24:      	csel	x11, x12, x11, hs
   97e28:      	eor	w12, w2, w10
   97e2c:      	cmp	w12, #0x0
   97e30:      	ccmn	w10, #0x1, #0x4, lt
   97e34:      	mov	w10, #0x800             ; =2048
   97e38:      	csel	x10, x10, xzr, gt
   97e3c:      	and	w12, w12, #0x10
   97e40:      	and	x13, x11, #0xffffffff00000000
   97e44:      	and	x13, x13, #0xffff00ffffffffff
   97e48:      	and	w14, w2, #0xff
   97e4c:      	orr	x13, x13, x14, lsl #40
   97e50:      	and	x11, x11, #0xfffffffe
   97e54:      	bfxil	x12, x26, #0, #1
   97e58:      	orr	x10, x10, x11
   97e5c:      	orr	x10, x12, x10
   97e60:      	orr	x26, x10, x13
   97e64:      	cmp	w9, #0xc0
   97e68:      	b.lo	0x97fc8 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x2c0>
   97e6c:      	and	w9, w9, #0x7
   97e70:      	str	w2, [x20, w9, uxtw #2]
   97e74:      	ldrb	w9, [x21, #0x6d]
   97e78:      	cmp	w9, #0xc0
   97e7c:      	b.lo	0x9802c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x324>
   97e80:      	and	x10, x9, #0x3
   97e84:      	ldr	w10, [x20, x10, lsl #2]
   97e88:      	lsl	w11, w9, #1
   97e8c:      	and	w11, w11, #0x8
   97e90:      	lsr	w11, w10, w11
   97e94:      	ldrb	w10, [x21, #0x6e]
   97e98:      	ubfx	w9, w9, #3, #3
   97e9c:      	tbnz	w10, #0x6, 0x97ec0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x1b8>
   97ea0:      	and	w10, w11, #0xff
   97ea4:      	str	w10, [x20, w9, uxtw #2]
   97ea8:      	mov	w9, w23
   97eac:      	orr	x23, x9, x8, lsl #32
   97eb0:      	ldr	x0, [x21, #0x80]!
   97eb4:      	ldp	x29, x30, [sp, #0x10]
   97eb8:      	add	sp, sp, #0x20
   97ebc:      	br	x0
   97ec0:      	ubfiz	x9, x9, #2, #32
   97ec4:      	and	w10, w11, #0xff
   97ec8:      	strh	w10, [x20, x9]
   97ecc:      	mov	w9, w23
   97ed0:      	orr	x23, x9, x8, lsl #32
   97ed4:      	ldr	x0, [x21, #0x80]!
   97ed8:      	ldp	x29, x30, [sp, #0x10]
   97edc:      	add	sp, sp, #0x20
   97ee0:      	br	x0
   97ee4:      	ubfx	w12, w10, #12, #8
   97ee8:      	add	x12, x20, w12, uxtw #4
   97eec:      	ldr	w13, [x12, #0x1a0]
   97ef0:      	eor	w13, w13, w11
   97ef4:      	orr	w8, w13, w8
   97ef8:      	cbnz	w8, 0x98084 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x37c>
   97efc:      	add	x8, x12, #0x1a0
   97f00:      	ldr	x24, [x8, #0x8]
   97f04:      	ldr	w10, [x24, w10, uxtw]
   97f08:      	ldr	w8, [x8, #0x4]
   97f0c:      	mov	w12, #0x2a              ; =42
   97f10:      	and	w8, w8, w12
   97f14:      	cmp	w8, #0xa
   97f18:      	csinv	w8, w11, wzr, eq
   97f1c:      	mov	x23, x11
   97f20:      	b	0x97d7c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x74>
   97f24:      	ubfx	w8, w10, #12, #8
   97f28:      	add	x8, x20, w8, uxtw #4
   97f2c:      	ldr	w13, [x8, #0x1a0]
   97f30:      	eor	w13, w13, w11
   97f34:      	orr	w12, w13, w12
   97f38:      	cbnz	w12, 0x980d0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x3c8>
   97f3c:      	add	x8, x8, #0x1a0
   97f40:      	ldr	x24, [x8, #0x8]
   97f44:      	ldr	w10, [x24, w10, uxtw]
   97f48:      	ldr	w8, [x8, #0x4]
   97f4c:      	mov	w9, #0x2a               ; =42
   97f50:      	and	w8, w8, w9
   97f54:      	cmp	w8, #0xa
   97f58:      	csinv	w8, w11, wzr, eq
   97f5c:      	mov	x23, x11
   97f60:      	b	0x97de0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xd8>
   97f64:      	ldp	w10, w11, [x21, #0x54]
   97f68:      	ubfx	x12, x10, #6, #6
   97f6c:      	ldr	w12, [x12, x20]
   97f70:      	ubfx	w13, w10, #12, #2
   97f74:      	lsl	w12, w12, w13
   97f78:      	and	x13, x10, #0x3f
   97f7c:      	ldr	w13, [x13, x20]
   97f80:      	add	w11, w13, w11
   97f84:      	add	w11, w12, w11
   97f88:      	ldrsb	w12, [x21, #0x4e]
   97f8c:      	and	w13, w11, #0xffff
   97f90:      	cmp	w12, #0x0
   97f94:      	csel	w11, w13, w11, lt
   97f98:      	ubfx	x10, x10, #17, #3
   97f9c:      	ldr	w10, [x19, x10, lsl #2]
   97fa0:      	add	w1, w11, w10
   97fa4:      	add	w10, w1, #0x3
   97fa8:      	eor	w10, w10, w1
   97fac:      	and	w11, w1, #0xfffff000
   97fb0:      	and	w10, w10, #0xfffff000
   97fb4:      	eor	w12, w11, w23
   97fb8:      	orr	w12, w10, w12
   97fbc:      	cbnz	w12, 0x9811c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x414>
   97fc0:      	add	x10, x24, x1
   97fc4:      	b	0x97e00 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xf8>
   97fc8:      	ldp	w9, w10, [x21, #0x54]
   97fcc:      	ubfx	x11, x9, #6, #6
   97fd0:      	ldr	w11, [x11, x20]
   97fd4:      	ubfx	w12, w9, #12, #2
   97fd8:      	lsl	w11, w11, w12
   97fdc:      	and	x12, x9, #0x3f
   97fe0:      	ldr	w12, [x12, x20]
   97fe4:      	add	w10, w12, w10
   97fe8:      	add	w10, w11, w10
   97fec:      	ldrsb	w11, [x21, #0x4e]
   97ff0:      	and	w12, w10, #0xffff
   97ff4:      	cmp	w11, #0x0
   97ff8:      	csel	w10, w12, w10, lt
   97ffc:      	ubfx	x9, x9, #17, #3
   98000:      	ldr	w9, [x19, x9, lsl #2]
   98004:      	add	w1, w10, w9
   98008:      	add	w9, w1, #0x3
   9800c:      	eor	w10, w9, w1
   98010:      	and	w9, w1, #0xfffff000
   98014:      	and	w10, w10, #0xfffff000
   98018:      	eor	w11, w9, w8
   9801c:      	orr	w11, w10, w11
   98020:      	cbnz	w11, 0x9815c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x454>
   98024:      	str	w2, [x24, x1]
   98028:      	b	0x97e74 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x16c>
   9802c:      	ldp	w11, w10, [x21, #0x74]
   98030:      	and	x12, x11, #0x3f
   98034:      	ldr	w12, [x12, x20]
   98038:      	ubfx	x13, x11, #6, #6
   9803c:      	ldr	w13, [x13, x20]
   98040:      	ubfx	w14, w11, #12, #2
   98044:      	lsl	w13, w13, w14
   98048:      	add	w10, w12, w10
   9804c:      	add	w12, w13, w10
   98050:      	ldrb	w10, [x21, #0x6e]
   98054:      	sxtb	w13, w10
   98058:      	and	w14, w12, #0xffff
   9805c:      	cmp	w13, #0x0
   98060:      	csel	w12, w14, w12, lt
   98064:      	ubfx	x11, x11, #17, #3
   98068:      	ldr	w11, [x19, x11, lsl #2]
   9806c:      	add	w11, w12, w11
   98070:      	and	w12, w11, #0xfffff000
   98074:      	cmp	w23, w12
   98078:      	b.ne	0x98198 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x490>
   9807c:      	ldrb	w11, [x24, w11, uxtw]
   98080:      	b	0x97e98 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x190>
   98084:      	ldr	w8, [x21, #0x8]
   98088:      	ldrb	w11, [x21, #0xc]
   9808c:      	sub	w11, w8, w11
   98090:      	str	w11, [x20, #0x24]
   98094:      	ldrb	w11, [x9, #0x110]
   98098:      	cmp	w11, #0x1
   9809c:      	b.ne	0x981d4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4cc>
   980a0:      	ldrb	w11, [x9, #0x13c]
   980a4:      	cmp	w11, #0x1
   980a8:      	b.ne	0x981d4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4cc>
   980ac:      	ldr	w11, [x9, #0x138]
   980b0:      	cmp	w11, w8
   980b4:      	b.ne	0x981d4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x4cc>
   980b8:      	mov	x24, #0x0               ; =0
   980bc:      	ldr	w10, [x9, #0x120]
   980c0:      	strb	wzr, [x9, #0x110]
   980c4:      	mov	w8, #-0x1               ; =-1
   980c8:      	mov	x23, x8
   980cc:      	b	0x97d7c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x74>
   980d0:      	ldr	w8, [x21, #0x28]
   980d4:      	ldrb	w11, [x21, #0x2c]
   980d8:      	sub	w11, w8, w11
   980dc:      	str	w11, [x20, #0x24]
   980e0:      	ldrb	w11, [x9, #0x110]
   980e4:      	cmp	w11, #0x1
   980e8:      	b.ne	0x9820c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x504>
   980ec:      	ldrb	w11, [x9, #0x13c]
   980f0:      	cmp	w11, #0x1
   980f4:      	b.ne	0x9820c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x504>
   980f8:      	ldr	w11, [x9, #0x138]
   980fc:      	cmp	w11, w8
   98100:      	b.ne	0x9820c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x504>
   98104:      	mov	x24, #0x0               ; =0
   98108:      	ldr	w10, [x9, #0x120]
   9810c:      	strb	wzr, [x9, #0x110]
   98110:      	mov	w8, #-0x1               ; =-1
   98114:      	mov	x23, x8
   98118:      	b	0x97de0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xd8>
   9811c:      	ubfx	w8, w1, #12, #8
   98120:      	add	x8, x20, w8, uxtw #4
   98124:      	ldr	w12, [x8, #0x1a0]
   98128:      	eor	w12, w12, w11
   9812c:      	orr	w10, w12, w10
   98130:      	cbnz	w10, 0x98248 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x540>
   98134:      	add	x8, x8, #0x1a0
   98138:      	ldr	w12, [x8, #0x4]
   9813c:      	ldr	x24, [x8, #0x8]
   98140:      	add	x10, x24, x1
   98144:      	mov	w8, #0x2a               ; =42
   98148:      	and	w8, w12, w8
   9814c:      	cmp	w8, #0xa
   98150:      	csinv	w8, w11, wzr, eq
   98154:      	mov	x23, x11
   98158:      	b	0x97e00 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xf8>
   9815c:      	ubfx	w12, w1, #12, #8
   98160:      	add	x11, x20, w12, uxtw #4
   98164:      	mov	w8, #0x11a0             ; =4512
   98168:      	add	x8, x11, x8
   9816c:      	ldr	w11, [x11, #0x11a0]
   98170:      	eor	w13, w11, w9
   98174:      	orr	w10, w13, w10
   98178:      	cbnz	w10, 0x98290 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x588>
   9817c:      	ldr	x24, [x8, #0x8]
   98180:      	ldr	w8, [x8, #0x4]
   98184:      	str	w2, [x24, x1]
   98188:      	tst	w8, #0x1
   9818c:      	csinv	w23, w9, wzr, ne
   98190:      	mov	x8, x9
   98194:      	b	0x97e74 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x16c>
   98198:      	ubfx	w8, w11, #12, #8
   9819c:      	add	x8, x20, w8, uxtw #4
   981a0:      	ldr	w13, [x8, #0x1a0]
   981a4:      	cmp	w13, w12
   981a8:      	b.ne	0x98308 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x600>
   981ac:      	add	x8, x8, #0x1a0
   981b0:      	ldr	x24, [x8, #0x8]
   981b4:      	ldrb	w11, [x24, w11, uxtw]
   981b8:      	ldr	w8, [x8, #0x4]
   981bc:      	mov	w13, #0x2a              ; =42
   981c0:      	and	w8, w8, w13
   981c4:      	cmp	w8, #0xa
   981c8:      	csinv	w8, w12, wzr, eq
   981cc:      	mov	x23, x12
   981d0:      	b	0x97e98 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x190>
   981d4:      	mov	w11, #0x1               ; =1
   981d8:      	strb	w11, [x9, #0x110]
   981dc:      	movi.2d	v0, #0000000000000000
   981e0:      	str	q0, [x20, #0x7120]
   981e4:      	str	w10, [x9, #0x130]
   981e8:      	mov	w10, #0x4               ; =4
   981ec:      	str	w10, [x9, #0x134]
   981f0:      	str	w8, [x9, #0x138]
   981f4:      	strb	wzr, [x9, #0x13c]
   981f8:      	mov	x23, #-0x1              ; =-1
   981fc:      	mov	x24, #0x0               ; =0
   98200:      	ldp	x29, x30, [sp, #0x10]
   98204:      	add	sp, sp, #0x20
   98208:      	b	0x140a1c <_vsnprintf+0x140a1c>
   9820c:      	mov	w11, #0x1               ; =1
   98210:      	strb	w11, [x9, #0x110]
   98214:      	movi.2d	v0, #0000000000000000
   98218:      	str	q0, [x20, #0x7120]
   9821c:      	str	w10, [x9, #0x130]
   98220:      	mov	w10, #0x4               ; =4
   98224:      	str	w10, [x9, #0x134]
   98228:      	str	w8, [x9, #0x138]
   9822c:      	strb	wzr, [x9, #0x13c]
   98230:      	add	x21, x21, #0x20
   98234:      	mov	x23, #-0x1              ; =-1
   98238:      	mov	x24, #0x0               ; =0
   9823c:      	ldp	x29, x30, [sp, #0x10]
   98240:      	add	sp, sp, #0x20
   98244:      	b	0x140a1c <_vsnprintf+0x140a1c>
   98248:      	mov	w8, #0xe                ; =14
   9824c:      	strb	w8, [sp, #0x4]
   98250:      	strb	wzr, [sp, #0x8]
   98254:      	ldr	w8, [x21, #0x48]
   98258:      	ldrb	w9, [x21, #0x4c]
   9825c:      	sub	w8, w8, w9
   98260:      	str	w8, [x20, #0x24]
   98264:      	add	x8, sp, #0x4
   98268:      	add	x0, x20, #0x180
   9826c:      	bl	0x81f44 <__ZN8fiberish3mem3Mmu9read_slowIjEENSt3__18expectedIT_NS0_9FaultCodeEEEj>
   98270:      	ldrb	w8, [sp, #0x8]
   98274:      	tbz	w8, #0x0, 0x98374 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x66c>
   98278:      	mov	x24, #0x0               ; =0
   9827c:      	ldrb	w9, [x21, #0x4d]
   98280:      	mov	w23, #-0x1              ; =-1
   98284:      	add	x10, sp, #0x4
   98288:      	mov	x8, x23
   9828c:      	b	0x97e00 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0xf8>
   98290:      	add	x10, x20, x12, lsl #4
   98294:      	ldr	w12, [x10, #0x1a0]
   98298:      	cmp	w12, w9
   9829c:      	b.ne	0x98354 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x64c>
   982a0:      	add	x12, x10, #0x1a0
   982a4:      	mov	w13, #-0x1              ; =-1
   982a8:      	str	w13, [x12]
   982ac:      	cmp	w11, w9
   982b0:      	b.eq	0x9835c <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x654>
   982b4:      	ldr	w8, [x10, #0x21a0]
   982b8:      	cmp	w8, w9
   982bc:      	b.ne	0x982cc <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x5c4>
   982c0:      	mov	w8, #0x21a0             ; =8608
   982c4:      	mov	w9, #-0x1               ; =-1
   982c8:      	str	w9, [x10, x8]
   982cc:      	mov	w8, #0xe                ; =14
   982d0:      	strh	w8, [sp, #0xe]
   982d4:      	ldr	w8, [x21, #0x48]
   982d8:      	ldrb	w9, [x21, #0x4c]
   982dc:      	sub	w8, w8, w9
   982e0:      	str	w8, [x20, #0x24]
   982e4:      	add	x8, sp, #0xe
   982e8:      	add	x0, x20, #0x180
   982ec:      	bl	0x81190 <__ZN8fiberish3mem3Mmu10write_slowIjEENSt3__18expectedIvNS0_9FaultCodeEEEjT_>
   982f0:      	ldrb	w8, [sp, #0xf]
   982f4:      	tbz	w8, #0x0, 0x98374 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x66c>
   982f8:      	mov	x24, #0x0               ; =0
   982fc:      	mov	w23, #-0x1              ; =-1
   98300:      	mov	x8, x23
   98304:      	b	0x97e74 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x16c>
   98308:      	ldr	w8, [x21, #0x68]
   9830c:      	ldrb	w12, [x21, #0x6c]
   98310:      	sub	w12, w8, w12
   98314:      	str	w12, [x20, #0x24]
   98318:      	ldrb	w12, [x27, #0x2d0]
   9831c:      	cmp	w12, #0x1
   98320:      	b.ne	0x983a0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x698>
   98324:      	ldrb	w12, [x27, #0x2fc]
   98328:      	cmp	w12, #0x1
   9832c:      	b.ne	0x983a0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x698>
   98330:      	ldr	w12, [x27, #0x2f8]
   98334:      	cmp	w12, w8
   98338:      	b.ne	0x983a0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x698>
   9833c:      	mov	x24, #0x0               ; =0
   98340:      	ldrb	w11, [x27, #0x2e0]
   98344:      	strb	wzr, [x27, #0x2d0]
   98348:      	mov	w8, #-0x1               ; =-1
   9834c:      	mov	x23, x8
   98350:      	b	0x97e98 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x190>
   98354:      	cmp	w11, w9
   98358:      	b.ne	0x982b4 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x5ac>
   9835c:      	mov	w11, #-0x1              ; =-1
   98360:      	str	w11, [x8]
   98364:      	ldr	w8, [x10, #0x21a0]
   98368:      	cmp	w8, w9
   9836c:      	b.eq	0x982c0 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x5b8>
   98370:      	b	0x982cc <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x5c4>
   98374:      	str	x26, [x20, #0x28]
   98378:      	ldrb	w8, [x27]
   9837c:      	tbnz	w8, #0x0, 0x98390 <__ZN8fiberish88SuperOpcode_4_043_OpMov_Load_Edx__OpMov_Load_Eax__OpGroup5_Ev_Inc_32_Flags__OpMovzx_ByteEPNS_8EmuStateEPNS_9DecodedOpExyyjy+0x688>
   98380:      	ldr	w8, [x21, #0x48]
   98384:      	ldrb	w9, [x21, #0x4c]
   98388:      	sub	w8, w8, w9
   9838c:      	str	w8, [x20, #0x24]
   98390:      	mov	x0, x22
   98394:      	ldp	x29, x30, [sp, #0x10]
   98398:      	add	sp, sp, #0x20
   9839c:      	ret
   983a0:      	mov	w9, #0x1                ; =1
   983a4:      	strb	w9, [x27, #0x2d0]
   983a8:      	movi.2d	v0, #0000000000000000
   983ac:      	str	q0, [x20, #0x7120]
   983b0:      	str	w11, [x27, #0x2f0]
   983b4:      	str	w9, [x27, #0x2f4]
   983b8:      	str	w8, [x27, #0x2f8]
   983bc:      	strb	wzr, [x27, #0x2fc]
   983c0:      	add	x21, x21, #0x60
   983c4:      	mov	x23, #-0x1              ; =-1
   983c8:      	mov	x24, #0x0               ; =0
   983cc:      	ldp	x29, x30, [sp, #0x10]
   983d0:      	add	sp, sp, #0x20
   983d4:      	b	0x140a1c <_vsnprintf+0x140a1c>
