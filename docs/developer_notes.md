# Developer notes

## Dispatch and control flow

The decoder lays out a `DecodedOp` stream for each basic block.
`DispatchWrapper` invokes an instruction's logic and uses `ATTR_MUSTTAIL`
to jump to the next handler on `LogicFlow::Continue`. Native builds use
`ATTR_PRESERVE_NONE` where the compiler supports it. On an exception or other
exit, the wrapper can reconstruct the current or next guest EIP from the
block's instruction metadata.
[Dispatch and exit paths](../libfibercpu/src/dispatch.h)

For a direct relative branch, the decoder adds the encoded displacement to
the instruction's end EIP. A 16-bit near branch wraps to 16 bits. The
runtime record stores the resulting target EIP for `GetControlTargetEip`.
A relative `CALL` pushes the instruction's end EIP as its return address.
JMP, taken Jcc, CALL, and RET can tail-call the next block through
`ResolveBranchTargetInline`. With budget left, its fast path checks that the
cached block is valid and starts at the requested EIP. The slow path looks
up the target in the block cache. On a cache miss, it returns to `X86_Run`,
which decodes the target.
[Decoder](../libfibercpu/src/decoder.cpp) · [Branch semantics](../libfibercpu/src/ops/ops_control_impl.h)

## Flags

PF describes the parity of the result's low byte. AF records the carry or
borrow between bits 3 and 4. For ADD, OF indicates that two operands with
the same sign produced a result of the opposite sign. For SUB, it indicates
that operands of opposite sign produced a result whose sign differs from
the destination's original sign.
[Flags cache and lazy parity](flags-cache-model.md)

## Immediate lengths

The decoder uses `kImmType` and `GetImmLength` to read trailing bytes. Near
`CALL` (`E8`), near `JMP` (`E9`), near Jcc (`0F 80` through `0F 8F`), and
`MOV r/m, imm` (`C7`) use type 7. The operand-size prefix selects two
bytes in a 32-bit code segment. Otherwise the decoder reads four bytes.
Short Jcc (`70` through `7F`) and short JMP (`EB`) use type 5 and read
one byte. The decoder sign-extends relative branch displacements when
it computes the target.
[Immediate table](../libfibercpu/src/decoder_lut.h) · [Decoder](../libfibercpu/src/decoder.cpp)

## Guest pointers

Use `X86_ResolvePtrForRead` or `X86_ResolvePtrForWrite` to resolve a guest
address through the MMU before copying bytes. For a copy that crosses a
guest page boundary, resolve each page in turn. For a writable mapping marked
`ForceWriteSlow`, the write API invalidates the page's decoded blocks before
returning the pointer.
[Native bindings](../libfibercpu/src/bindings.cpp) · [Managed copy paths](../Fiberish.Core/Core/Engine.cs)
