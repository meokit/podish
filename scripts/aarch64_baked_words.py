"""Canonical baking keys for AArch64 instructions with block-bound addresses."""


def canonicalize_baked_word(word):
    # Match DecodeFields in libfibercpu/src/aarch64/decoder.h, including
    # B.cond's fixed bit 4 and the reserved SIMD literal opc == 3.
    if (word & 0x7c000000) == 0x14000000:  # B, BL
        return word & ~0x03ffffff
    if (word & 0xff000010) == 0x54000000:  # B.cond
        return word & ~0x00ffffe0
    if (word & 0x7e000000) == 0x34000000:  # CBZ, CBNZ
        return word & ~0x00ffffe0
    if (word & 0x7e000000) == 0x36000000:  # TBZ, TBNZ
        return word & ~0x0007ffe0
    if (word & 0x1f000000) == 0x10000000:  # ADR, ADRP
        return word & ~0x60ffffe0
    if (word & 0x3b000000) == 0x18000000 and (word & 0xc4000000) != 0xc4000000:
        return word & ~0x00ffffe0  # Literal load or prefetch
    return word
