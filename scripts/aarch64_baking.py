"""Immediate fields used by AArch64 plans, mirroring PartialImmediateMask in baked.h."""

from collections import Counter
from functools import lru_cache

from aarch64_baked_words import canonicalize_baked_word


# The flags, registers, size, addressing mode, and immediate shift stay baked.
SCALAR_FIELDS = (
    (0x1f800000, 0x11000000, 0xfff << 10),
    (0x3b000000, 0x39000000, 0xfff << 10),
    (0x3a000000, 0x28000000, 0x7f << 15),
    (0x3b200400, 0x38000400, 0x1ff << 12),
    (0x1f800000, 0x12800000, 0xffff << 5),
    (0x1f800000, 0x12000000, 0x1fff << 10),
)

BRANCH_FIELDS = (
    (0x7c000000, 0x14000000),
    (0xff000010, 0x54000000),
    (0x7e000000, 0x34000000),
    (0x7e000000, 0x36000000),
)
BRANCH_REGISTER = (0xd61f0000, 0xd63f0000, 0xd65f0000)


@lru_cache(maxsize=65536)
def partial_shape(word):
    for mask, value, immediate in SCALAR_FIELDS:
        if word & mask == value:
            if value == 0x12000000:
                n = (word >> 22) & 1
                pattern = (n << 6) | (~(word >> 10) & 63)
                # A reserved logical immediate can otherwise share a key
                # with valid encodings once all 13 immediate bits are cleared.
                if (n and not word >> 31) or not (pattern & (pattern - 1)):
                    return None
            return word & ~immediate
    return None


def is_branch(word):
    return (word & 0xfffffc1f) in BRANCH_REGISTER or any(
        word & mask == value for mask, value in BRANCH_FIELDS)


def pair_key(row):
    return (row.get("dynamic_mask", 0), canonicalize_baked_word(row["first"]),
            canonicalize_baked_word(row["second"]))


def pair_shapes(first, second):
    first, second = canonicalize_baked_word(first), canonicalize_baked_word(second)
    a, b = partial_shape(first), partial_shape(second)
    if a is not None:
        yield 1, a, second
    if b is not None:
        yield 2, first, b
    if a is not None and b is not None:
        yield 3, a, b


def partial_words(counts, exact_words, budget):
    if not budget:
        return []
    residual = Counter()
    distinct = Counter()
    for word, count in counts.items():
        shape = partial_shape(word)
        if shape is not None:
            distinct[shape] += 1
            if word not in exact_words:
                residual[shape] += count
    return [shape for shape, _ in sorted(residual.items(), key=lambda item: (-item[1], item[0]))
            if distinct[shape] >= 2][:budget]


def partial_pairs(profiles, families, budget):
    if not budget:
        return []
    # Each candidate needs two distinct canonical pairs, across or within captures.
    # Pair histograms in legacy captures only include integer-start pairs.
    evidence = {}
    repeated = set()
    weights = Counter()
    sizes = Counter(families)
    for profile, family in zip(profiles, families, strict=True):
        counts = Counter()
        if profile.get("version") == 2:
            # Captures list bakeable words separately. This admits memory starts
            # while excluding unhandled neighbors and branches in the first slot.
            known = {canonicalize_baked_word(row["word"]) for row in profile["entries"]}
            starts = {word for word in known if not is_branch(word)}
            for block in profile["blocks"]:
                words = [canonicalize_baked_word(word) for word in block["words"]]
                for first, second in zip(words, words[1:]):
                    if first in starts and second in known:
                        counts[first, second] += block["count"]
        else:
            for row in profile.get("pairs", []):
                counts[canonicalize_baked_word(row["first"]), canonicalize_baked_word(row["second"])] += row["count"]
        total = counts.total() * sizes[family]
        for pair, count in counts.items():
            for key in pair_shapes(*pair):
                if evidence.setdefault(key, pair) != pair:
                    repeated.add(key)
                weights[key] += count / total
    return [
        dict(first=first, second=second, dynamic_mask=mask,
             count=max(1, round(weights[mask, first, second] * 100_000_000)))
        for mask, first, second in sorted(repeated, key=lambda key: (-weights[key], key))[:budget]
    ]
