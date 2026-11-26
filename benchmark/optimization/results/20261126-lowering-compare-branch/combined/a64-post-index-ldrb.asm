000000000001cc3c T unsigned long long fiberish::aarch64::detail::LoadStoreHandler<(fiberish::aarch64::Kind)21, 8u, 1, 1, -1, 0>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

combined-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

000000000001cc3c <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   1cc3c:      	ldr	x11, [x21, #0x8]
   1cc40:      	ubfx	w13, w11, #17, #5
   1cc44:      	ubfx	x10, x11, #22, #5
   1cc48:      	add	x8, x20, x10, lsl #3
   1cc4c:      	add	x9, x20, #0xf8
   1cc50:      	cmp	w10, #0x1f
   1cc54:      	csel	x12, x9, x8, eq
   1cc58:      	ldr	x12, [x12]
   1cc5c:      	cset	w14, ne
   1cc60:      	cmp	w10, w13
   1cc64:      	csel	w14, wzr, w14, ne
   1cc68:      	tst	x12, #0xf
   1cc6c:      	ccmp	w10, #0x1f, #0x0, ne
   1cc70:      	b.eq	0x1ccf4 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1cc74:      	tbnz	w14, #0x0, 0x1ccf4 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1cc78:      	lsr	x14, x12, #39
   1cc7c:      	cbnz	x14, 0x1ccf4 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1cc80:      	lsr	x15, x12, #12
   1cc84:      	cmp	w15, w24
   1cc88:      	b.ne	0x1ccc8 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x8c>
   1cc8c:      	lsl	x14, x11, #47
   1cc90:      	cmp	w13, #0x1f
   1cc94:      	b.eq	0x1cca4 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x68>
   1cc98:      	ldrb	w13, [x25, x12]
   1cc9c:      	ubfx	x11, x11, #17, #5
   1cca0:      	str	x13, [x20, x11, lsl #3]
   1cca4:      	add	x11, x12, x14, asr #47
   1cca8:      	cmp	w10, #0x1f
   1ccac:      	b.ne	0x1ccbc <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   1ccb0:      	str	x11, [x9]
   1ccb4:      	ldr	x0, [x21, #0x10]!
   1ccb8:      	br	x0
   1ccbc:      	str	x11, [x8]
   1ccc0:      	ldr	x0, [x21, #0x10]!
   1ccc4:      	br	x0
   1ccc8:      	ubfx	x14, x12, #12, #10
   1cccc:      	add	x16, x23, x14, lsl #4
   1ccd0:      	ldr	x14, [x16]
   1ccd4:      	cmp	w14, w15
   1ccd8:      	b.ne	0x1ccf4 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xb8>
   1ccdc:      	ldr	x25, [x16, #0x8]
   1cce0:      	mov	x24, x14
   1cce4:      	lsl	x14, x11, #47
   1cce8:      	cmp	w13, #0x1f
   1ccec:      	b.ne	0x1cc98 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x5c>
   1ccf0:      	b	0x1cca4 <__ZN8fiberish7aarch646detail16LoadStoreHandlerILNS0_4KindE21ELj8ELi1ELi1ELin1ELi0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x68>
   1ccf4:      	b	0x5498f4 <__ZN8fiberish7aarch646detail15MemoryOpGenericERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
