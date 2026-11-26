0000000000039154 T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 0u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

combined-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000039154 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   39154:      	ldr	w9, [x21, #0x8]
   39158:      	ubfx	w8, w9, #5, #5
   3915c:      	ubfx	w9, w9, #10, #5
   39160:      	cmp	w8, #0x1f
   39164:      	b.ne	0x3917c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x28>
   39168:      	mov	x8, #0x0                ; =0
   3916c:      	cmp	w9, #0x1f
   39170:      	b.eq	0x39188 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x34>
   39174:      	ldr	x9, [x20, w9, uxtw #3]
   39178:      	b	0x3918c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x38>
   3917c:      	ldr	x8, [x20, w8, uxtw #3]
   39180:      	cmp	w9, #0x1f
   39184:      	b.ne	0x39174 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   39188:      	mov	x9, #0x0                ; =0
   3918c:      	cmp	x8, x9
   39190:      	mrs	x26, NZCV
   39194:      	b.eq	0x391a0 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x4c>
   39198:      	ldr	x0, [x21, #0x30]!
   3919c:      	br	x0
   391a0:      	cbz	x22, 0x391d4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   391a4:      	ldr	x9, [x21, #0x20]
   391a8:      	ldr	x8, [x21, #0x40]
   391ac:      	ldp	x0, x10, [x8]
   391b0:      	tbz	x10, #0x3f, 0x391d4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   391b4:      	and	x11, x10, #0x7fffffffff
   391b8:      	cmp	x11, x9
   391bc:      	ubfx	x9, x10, #39, #8
   391c0:      	ccmp	x22, x9, #0x0, eq
   391c4:      	b.lo	0x391d4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   391c8:      	sub	x22, x22, x9
   391cc:      	add	x21, x8, #0x20
   391d0:      	br	x0
   391d4:      	ldr	x8, [x20, #0x348]
   391d8:      	str	wzr, [x8, #0x28]
   391dc:      	ldr	x9, [x21, #0x20]
   391e0:      	str	x9, [x8, #0x30]
   391e4:      	add	x21, x21, #0x10
   391e8:      	b	0x5497e0 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
0000000000039614 T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 8u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

combined-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000039614 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   39614:      	ldr	w9, [x21, #0x8]
   39618:      	ubfx	w8, w9, #5, #5
   3961c:      	ubfx	w9, w9, #10, #5
   39620:      	cmp	w8, #0x1f
   39624:      	b.ne	0x3963c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x28>
   39628:      	mov	x8, #0x0                ; =0
   3962c:      	cmp	w9, #0x1f
   39630:      	b.eq	0x39648 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x34>
   39634:      	ldr	x9, [x20, w9, uxtw #3]
   39638:      	b	0x3964c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x38>
   3963c:      	ldr	x8, [x20, w8, uxtw #3]
   39640:      	cmp	w9, #0x1f
   39644:      	b.ne	0x39634 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   39648:      	mov	x9, #0x0                ; =0
   3964c:      	cmp	x8, x9
   39650:      	mrs	x26, NZCV
   39654:      	b.hi	0x39660 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x4c>
   39658:      	ldr	x0, [x21, #0x30]!
   3965c:      	br	x0
   39660:      	cbz	x22, 0x39694 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   39664:      	ldr	x9, [x21, #0x20]
   39668:      	ldr	x8, [x21, #0x40]
   3966c:      	ldp	x0, x10, [x8]
   39670:      	tbz	x10, #0x3f, 0x39694 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   39674:      	and	x11, x10, #0x7fffffffff
   39678:      	cmp	x11, x9
   3967c:      	ubfx	x9, x10, #39, #8
   39680:      	ccmp	x22, x9, #0x0, eq
   39684:      	b.lo	0x39694 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   39688:      	sub	x22, x22, x9
   3968c:      	add	x21, x8, #0x20
   39690:      	br	x0
   39694:      	ldr	x8, [x20, #0x348]
   39698:      	str	wzr, [x8, #0x28]
   3969c:      	ldr	x9, [x21, #0x20]
   396a0:      	str	x9, [x8, #0x30]
   396a4:      	add	x21, x21, #0x10
   396a8:      	b	0x5497e0 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
