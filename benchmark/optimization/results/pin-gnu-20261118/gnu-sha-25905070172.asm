
artifacts/pin-gnu-20261118/gnu-cli/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000000c7cd0 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj>:
   c7cd0:      	ldr	w8, [x20, #0x10]
   c7cd4:      	ldr	w9, [x20, #0x20]
   c7cd8:      	add	w8, w9, w8
   c7cdc:      	ldr	w9, [x21, #0x18]
   c7ce0:      	ldr	w10, [x20, #0x40]
   c7ce4:      	add	w9, w9, w10
   c7ce8:      	add	w8, w8, w9
   c7cec:      	add	w10, w8, #0x3
   c7cf0:      	and	w9, w8, #0xfffff000
   c7cf4:      	eor	w10, w10, w8
   c7cf8:      	and	w10, w10, #0xfffff000
   c7cfc:      	eor	w11, w9, w23
   c7d00:      	orr	w11, w10, w11
   c7d04:      	cbnz	w11, 0xc7d2c <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj+0x5c>
   c7d08:      	lsr	x10, x23, #32
   c7d0c:      	ldr	w8, [x24, w8, uxtw]
   c7d10:      	ldr	w9, [x20, #0xc]
   c7d14:      	add	w8, w9, w8
   c7d18:      	str	w8, [x20, #0xc]
   c7d1c:      	mov	w8, w23
   c7d20:      	orr	x23, x8, x10, lsl #32
   c7d24:      	ldr	x0, [x21, #0x20]!
   c7d28:      	br	x0
   c7d2c:      	ubfx	w11, w8, #12, #8
   c7d30:      	add	x11, x20, w11, uxtw #4
   c7d34:      	ldr	w12, [x11, #0x1a0]
   c7d38:      	eor	w12, w12, w9
   c7d3c:      	orr	w10, w12, w10
   c7d40:      	cbnz	w10, 0xc7d6c <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj+0x9c>
   c7d44:      	add	x10, x11, #0x1a0
   c7d48:      	ldr	x24, [x10, #0x8]
   c7d4c:      	ldr	w8, [x24, w8, uxtw]
   c7d50:      	ldr	w10, [x10, #0x4]
   c7d54:      	mov	w11, #0x2a              ; =42
   c7d58:      	and	w10, w10, w11
   c7d5c:      	cmp	w10, #0xa
   c7d60:      	csinv	w10, w9, wzr, eq
   c7d64:      	mov	x23, x9
   c7d68:      	b	0xc7d10 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj+0x40>
   c7d6c:      	add	x9, x20, #0x7, lsl #12  ; =0x7000
   c7d70:      	ldr	w10, [x21, #0x8]
   c7d74:      	ldrb	w11, [x21, #0xc]
   c7d78:      	sub	w11, w10, w11
   c7d7c:      	str	w11, [x20, #0x24]
   c7d80:      	ldrb	w11, [x9, #0x110]
   c7d84:      	cmp	w11, #0x1
   c7d88:      	b.ne	0xc7dbc <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj+0xec>
   c7d8c:      	ldrb	w11, [x9, #0x13c]
   c7d90:      	cmp	w11, #0x1
   c7d94:      	b.ne	0xc7dbc <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj+0xec>
   c7d98:      	ldr	w11, [x9, #0x138]
   c7d9c:      	cmp	w11, w10
   c7da0:      	b.ne	0xc7dbc <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj+0xec>
   c7da4:      	mov	x24, #0x0               ; =0
   c7da8:      	ldr	w8, [x9, #0x120]
   c7dac:      	strb	wzr, [x9, #0x110]
   c7db0:      	mov	w10, #-0x1              ; =-1
   c7db4:      	mov	x23, x10
   c7db8:      	b	0xc7d10 <__ZN8fiberish15DispatchWrapperIXadL_ZNS_2op13OpAdd_GvEv_NFEPNS_8EmuStateEPNS_9DecodedOpEPNS_3mem8MicroTLBEjPjRyEENS_18ExactOperandPolicyILy25905070172ELj0EEEEExS3_S5_xyyjyjj+0x40>
   c7dbc:      	mov	w11, #0x1               ; =1
   c7dc0:      	strb	w11, [x9, #0x110]
   c7dc4:      	movi.2d	v0, #0000000000000000
   c7dc8:      	str	q0, [x20, #0x7120]
   c7dcc:      	str	w8, [x9, #0x130]
   c7dd0:      	mov	w8, #0x4                ; =4
   c7dd4:      	str	w8, [x9, #0x134]
   c7dd8:      	str	w10, [x9, #0x138]
   c7ddc:      	strb	wzr, [x9, #0x13c]
   c7de0:      	mov	x23, #-0x1              ; =-1
   c7de4:      	mov	x24, #0x0               ; =0
   c7de8:      	b	0x1514b8 <_vsnprintf+0x1514b8>
