0000000000031fe4 T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 0u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

a64-native-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000031fe4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   31fe4:      	ldr	w9, [x21, #0x8]
   31fe8:      	ubfx	w8, w9, #5, #5
   31fec:      	ubfx	w9, w9, #10, #5
   31ff0:      	cmp	w8, #0x1f
   31ff4:      	b.ne	0x3200c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x28>
   31ff8:      	mov	x8, #0x0                ; =0
   31ffc:      	cmp	w9, #0x1f
   32000:      	b.eq	0x32018 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x34>
   32004:      	ldr	x9, [x20, w9, uxtw #3]
   32008:      	b	0x3201c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x38>
   3200c:      	ldr	x8, [x20, w8, uxtw #3]
   32010:      	cmp	w9, #0x1f
   32014:      	b.ne	0x32004 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   32018:      	mov	x9, #0x0                ; =0
   3201c:      	cmp	x8, x9
   32020:      	mrs	x26, NZCV
   32024:      	b.eq	0x32030 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x4c>
   32028:      	ldr	x0, [x21, #0x30]!
   3202c:      	br	x0
   32030:      	cbz	x22, 0x32064 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32034:      	ldr	x9, [x21, #0x20]
   32038:      	ldr	x8, [x21, #0x40]
   3203c:      	ldp	x0, x10, [x8]
   32040:      	tbz	x10, #0x3f, 0x32064 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32044:      	and	x11, x10, #0x7fffffffff
   32048:      	cmp	x11, x9
   3204c:      	ubfx	x9, x10, #39, #8
   32050:      	ccmp	x22, x9, #0x0, eq
   32054:      	b.lo	0x32064 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj0EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32058:      	sub	x22, x22, x9
   3205c:      	add	x21, x8, #0x20
   32060:      	br	x0
   32064:      	ldr	x8, [x20, #0x348]
   32068:      	str	wzr, [x8, #0x28]
   3206c:      	ldr	x9, [x21, #0x20]
   32070:      	str	x9, [x8, #0x30]
   32074:      	add	x21, x21, #0x10
   32078:      	b	0x541af8 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
00000000000324a4 T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 8u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

a64-native-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

00000000000324a4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   324a4:      	ldr	w9, [x21, #0x8]
   324a8:      	ubfx	w8, w9, #5, #5
   324ac:      	ubfx	w9, w9, #10, #5
   324b0:      	cmp	w8, #0x1f
   324b4:      	b.ne	0x324cc <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x28>
   324b8:      	mov	x8, #0x0                ; =0
   324bc:      	cmp	w9, #0x1f
   324c0:      	b.eq	0x324d8 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x34>
   324c4:      	ldr	x9, [x20, w9, uxtw #3]
   324c8:      	b	0x324dc <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x38>
   324cc:      	ldr	x8, [x20, w8, uxtw #3]
   324d0:      	cmp	w9, #0x1f
   324d4:      	b.ne	0x324c4 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   324d8:      	mov	x9, #0x0                ; =0
   324dc:      	cmp	x8, x9
   324e0:      	mrs	x26, NZCV
   324e4:      	b.hi	0x324f0 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x4c>
   324e8:      	ldr	x0, [x21, #0x30]!
   324ec:      	br	x0
   324f0:      	cbz	x22, 0x32524 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   324f4:      	ldr	x9, [x21, #0x20]
   324f8:      	ldr	x8, [x21, #0x40]
   324fc:      	ldp	x0, x10, [x8]
   32500:      	tbz	x10, #0x3f, 0x32524 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32504:      	and	x11, x10, #0x7fffffffff
   32508:      	cmp	x11, x9
   3250c:      	ubfx	x9, x10, #39, #8
   32510:      	ccmp	x22, x9, #0x0, eq
   32514:      	b.lo	0x32524 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj8EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32518:      	sub	x22, x22, x9
   3251c:      	add	x21, x8, #0x20
   32520:      	br	x0
   32524:      	ldr	x8, [x20, #0x348]
   32528:      	str	wzr, [x8, #0x28]
   3252c:      	ldr	x9, [x21, #0x20]
   32530:      	str	x9, [x8, #0x30]
   32534:      	add	x21, x21, #0x10
   32538:      	b	0x541af8 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
0000000000032704 T unsigned long long fiberish::aarch64::detail::CompareBranchHandler<(fiberish::aarch64::Kind)3, 64u, 12u>(fiberish::aarch64::State&, fiberish::aarch64::detail::DecodedOp const*, unsigned long long, fiberish::mem::SoftTlb const*, unsigned long long, unsigned long, unsigned int)

a64-native-release/libfibercpu.dylib:	file format mach-o arm64

Disassembly of section __TEXT,__text:

0000000000032704 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>:
   32704:      	ldr	w9, [x21, #0x8]
   32708:      	ubfx	w8, w9, #5, #5
   3270c:      	ubfx	w9, w9, #10, #5
   32710:      	cmp	w8, #0x1f
   32714:      	b.ne	0x3272c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x28>
   32718:      	mov	x8, #0x0                ; =0
   3271c:      	cmp	w9, #0x1f
   32720:      	b.eq	0x32738 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x34>
   32724:      	ldr	x9, [x20, w9, uxtw #3]
   32728:      	b	0x3273c <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x38>
   3272c:      	ldr	x8, [x20, w8, uxtw #3]
   32730:      	cmp	w9, #0x1f
   32734:      	b.ne	0x32724 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x20>
   32738:      	mov	x9, #0x0                ; =0
   3273c:      	cmp	x8, x9
   32740:      	mrs	x26, NZCV
   32744:      	b.gt	0x32750 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x4c>
   32748:      	ldr	x0, [x21, #0x30]!
   3274c:      	br	x0
   32750:      	cbz	x22, 0x32784 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32754:      	ldr	x9, [x21, #0x20]
   32758:      	ldr	x8, [x21, #0x40]
   3275c:      	ldp	x0, x10, [x8]
   32760:      	tbz	x10, #0x3f, 0x32784 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32764:      	and	x11, x10, #0x7fffffffff
   32768:      	cmp	x11, x9
   3276c:      	ubfx	x9, x10, #39, #8
   32770:      	ccmp	x22, x9, #0x0, eq
   32774:      	b.lo	0x32784 <__ZN8fiberish7aarch646detail20CompareBranchHandlerILNS0_4KindE3ELj64ELj12EEEyRNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj+0x80>
   32778:      	sub	x22, x22, x9
   3277c:      	add	x21, x8, #0x20
   32780:      	br	x0
   32784:      	ldr	x8, [x20, #0x348]
   32788:      	str	wzr, [x8, #0x28]
   3278c:      	ldr	x9, [x21, #0x20]
   32790:      	str	x9, [x8, #0x30]
   32794:      	add	x21, x21, #0x10
   32798:      	b	0x541af8 <__ZN8fiberish7aarch646detail23ResolveBranchTargetSlowERNS0_5StateEPKNS1_9DecodedOpEyPKNS_3mem7SoftTlbEymj>
