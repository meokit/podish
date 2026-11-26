000000000003215c T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 0u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

a64-pair-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

000000000003215c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   3215c:      	ldr	w9, [x21, #0x8]
   32160:      	ubfx	w8, w9, #5, #5
   32164:      	ubfx	w9, w9, #10, #5
   32168:      	cmp	w8, #0x1f
   3216c:      	b.ne	0x321c0 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x64>
   32170:      	mov	x8, #0x0                ; =0
   32174:      	cmp	w9, #0x1f
   32178:      	b.eq	0x321cc <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x70>
   3217c:      	ldr	x9, [x20, w9, uxtw #3]
   32180:      	subs	x8, x8, x9
   32184:      	mrs	x26, NZCV
   32188:      	tbz	w26, #0x1e, 0x321dc <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   3218c:      	cbz	x22, 0x321e4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x88>
   32190:      	ldr	x9, [x21, #0x20]
   32194:      	ldr	x8, [x21, #0x40]
   32198:      	ldp	x0, x10, [x8]
   3219c:      	tbz	x10, #0x3f, 0x321e4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x88>
   321a0:      	and	x11, x10, #0x7fffffffff
   321a4:      	cmp	x11, x9
   321a8:      	ubfx	x9, x10, #39, #8
   321ac:      	ccmp	x22, x9, #0x0, eq
   321b0:      	b.lo	0x321e4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x88>
   321b4:      	sub	x22, x22, x9
   321b8:      	add	x21, x8, #0x20
   321bc:      	br	x0
   321c0:      	ldr	x8, [x20, w8, uxtw #3]
   321c4:      	cmp	w9, #0x1f
   321c8:      	b.ne	0x3217c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   321cc:      	mov	x9, #0x0                ; =0
   321d0:      	subs	x8, x8, x9
   321d4:      	mrs	x26, NZCV
   321d8:      	tbnz	w26, #0x1e, 0x3218c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x30>
   321dc:      	ldr	x0, [x21, #0x30]!
   321e0:      	br	x0
   321e4:      	ldr	x8, [x20, #0x348]
   321e8:      	str	wzr, [x8, #0x28]
   321ec:      	ldr	x9, [x21, #0x20]
   321f0:      	str	x9, [x8, #0x30]
   321f4:      	add	x21, x21, #0x10
   321f8:      	b	0x543790 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
000000000003265c T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 8u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

a64-pair-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

000000000003265c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   3265c:      	ldr	w9, [x21, #0x8]
   32660:      	ubfx	w8, w9, #5, #5
   32664:      	ubfx	w9, w9, #10, #5
   32668:      	cmp	w8, #0x1f
   3266c:      	b.ne	0x326a0 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x44>
   32670:      	mov	x8, #0x0                ; =0
   32674:      	cmp	w9, #0x1f
   32678:      	b.eq	0x326ac <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x50>
   3267c:      	ldr	x9, [x20, w9, uxtw #3]
   32680:      	subs	x8, x8, x9
   32684:      	mrs	x26, NZCV
   32688:      	and	w8, w26, #0x60000000
   3268c:      	mov	w9, #0x20000000         ; =536870912
   32690:      	cmp	w8, w9
   32694:      	b.eq	0x326c8 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x6c>
   32698:      	ldr	x0, [x21, #0x30]!
   3269c:      	br	x0
   326a0:      	ldr	x8, [x20, w8, uxtw #3]
   326a4:      	cmp	w9, #0x1f
   326a8:      	b.ne	0x3267c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   326ac:      	mov	x9, #0x0                ; =0
   326b0:      	subs	x8, x8, x9
   326b4:      	mrs	x26, NZCV
   326b8:      	and	w8, w26, #0x60000000
   326bc:      	mov	w9, #0x20000000         ; =536870912
   326c0:      	cmp	w8, w9
   326c4:      	b.ne	0x32698 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x3c>
   326c8:      	cbz	x22, 0x326fc <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xa0>
   326cc:      	ldr	x9, [x21, #0x20]
   326d0:      	ldr	x8, [x21, #0x40]
   326d4:      	ldp	x0, x10, [x8]
   326d8:      	tbz	x10, #0x3f, 0x326fc <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xa0>
   326dc:      	and	x11, x10, #0x7fffffffff
   326e0:      	cmp	x11, x9
   326e4:      	ubfx	x9, x10, #39, #8
   326e8:      	ccmp	x22, x9, #0x0, eq
   326ec:      	b.lo	0x326fc <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0xa0>
   326f0:      	sub	x22, x22, x9
   326f4:      	add	x21, x8, #0x20
   326f8:      	br	x0
   326fc:      	ldr	x8, [x20, #0x348]
   32700:      	str	wzr, [x8, #0x28]
   32704:      	ldr	x9, [x21, #0x20]
   32708:      	str	x9, [x8, #0x30]
   3270c:      	add	x21, x21, #0x10
   32710:      	b	0x543790 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
000000000003292c T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 12u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

a64-pair-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

000000000003292c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   3292c:      	ldr	w9, [x21, #0x8]
   32930:      	ubfx	w8, w9, #5, #5
   32934:      	ubfx	w9, w9, #10, #5
   32938:      	cmp	w8, #0x1f
   3293c:      	b.ne	0x32960 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x34>
   32940:      	mov	x8, #0x0                ; =0
   32944:      	cmp	w9, #0x1f
   32948:      	b.eq	0x3296c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x40>
   3294c:      	ldr	x9, [x20, w9, uxtw #3]
   32950:      	subs	x8, x8, x9
   32954:      	mrs	x26, NZCV
   32958:      	tbz	w26, #0x1e, 0x3297c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x50>
   3295c:      	b	0x329c0 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x94>
   32960:      	ldr	x8, [x20, w8, uxtw #3]
   32964:      	cmp	w9, #0x1f
   32968:      	b.ne	0x3294c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   3296c:      	mov	x9, #0x0                ; =0
   32970:      	subs	x8, x8, x9
   32974:      	mrs	x26, NZCV
   32978:      	tbnz	w26, #0x1e, 0x329c0 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x94>
   3297c:      	lsr	w8, w26, #31
   32980:      	ubfx	w9, w26, #28, #1
   32984:      	cmp	w8, w9
   32988:      	b.ne	0x329c0 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x94>
   3298c:      	cbz	x22, 0x329c8 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x9c>
   32990:      	ldr	x9, [x21, #0x20]
   32994:      	ldr	x8, [x21, #0x40]
   32998:      	ldp	x0, x10, [x8]
   3299c:      	tbz	x10, #0x3f, 0x329c8 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x9c>
   329a0:      	and	x11, x10, #0x7fffffffff
   329a4:      	cmp	x11, x9
   329a8:      	ubfx	x9, x10, #39, #8
   329ac:      	ccmp	x22, x9, #0x0, eq
   329b0:      	b.lo	0x329c8 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x9c>
   329b4:      	sub	x22, x22, x9
   329b8:      	add	x21, x8, #0x20
   329bc:      	br	x0
   329c0:      	ldr	x0, [x21, #0x30]!
   329c4:      	br	x0
   329c8:      	ldr	x8, [x20, #0x348]
   329cc:      	str	wzr, [x8, #0x28]
   329d0:      	ldr	x9, [x21, #0x20]
   329d4:      	str	x9, [x8, #0x30]
   329d8:      	add	x21, x21, #0x10
   329dc:      	b	0x543790 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
