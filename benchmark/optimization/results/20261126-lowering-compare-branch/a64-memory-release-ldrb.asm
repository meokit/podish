000000000001bae4 T unsigned long long fiberish::aarch64::detail::LoadStoreHandler<(fiberish::aarch64::Kind)21, 8u, 1, 1, -1, 0>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

a64-memory-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

000000000001bae4 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   1bae4:      	ldr	x11, [x21, #0x8]
   1bae8:      	ubfx	w13, w11, #17, #5
   1baec:      	ubfx	x10, x11, #22, #5
   1baf0:      	add	x8, x20, x10, lsl #3
   1baf4:      	add	x9, x20, #0xf8
   1baf8:      	cmp	w10, #0x1f
   1bafc:      	csel	x12, x9, x8, eq
   1bb00:      	ldr	x12, [x12]
   1bb04:      	cset	w14, ne
   1bb08:      	cmp	w10, w13
   1bb0c:      	csel	w14, wzr, w14, ne
   1bb10:      	tst	x12, #0xf
   1bb14:      	ccmp	w10, #0x1f, #0x0, ne
   1bb18:      	b.eq	0x1bb9c <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1bb1c:      	tbnz	w14, #0x0, 0x1bb9c <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1bb20:      	lsr	x14, x12, #39
   1bb24:      	cbnz	x14, 0x1bb9c <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1bb28:      	lsr	x15, x12, #12
   1bb2c:      	cmp	w15, w24
   1bb30:      	b.ne	0x1bb70 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x8c>
   1bb34:      	lsl	x14, x11, #47
   1bb38:      	cmp	w13, #0x1f
   1bb3c:      	b.eq	0x1bb4c <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x68>
   1bb40:      	ldrb	w13, [x25, x12]
   1bb44:      	ubfx	x11, x11, #17, #5
   1bb48:      	str	x13, [x20, x11, lsl #3]
   1bb4c:      	add	x11, x12, x14, asr #47
   1bb50:      	cmp	w10, #0x1f
   1bb54:      	b.ne	0x1bb64 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   1bb58:      	str	x11, [x9]
   1bb5c:      	ldr	x0, [x21, #0x10]!
   1bb60:      	br	x0
   1bb64:      	str	x11, [x8]
   1bb68:      	ldr	x0, [x21, #0x10]!
   1bb6c:      	br	x0
   1bb70:      	ubfx	x14, x12, #12, #10
   1bb74:      	add	x16, x23, x14, lsl #4
   1bb78:      	ldr	x14, [x16]
   1bb7c:      	cmp	w14, w15
   1bb80:      	b.ne	0x1bb9c <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1bb84:      	ldr	x25, [x16, #0x8]
   1bb88:      	mov	x24, x14
   1bb8c:      	lsl	x14, x11, #47
   1bb90:      	cmp	w13, #0x1f
   1bb94:      	b.ne	0x1bb40 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x5c>
   1bb98:      	b	0x1bb4c <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x68>
   1bb9c:      	b	0x5469ec <__ZN8fiberish7aarch646detail15MemoryOpGenericERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
